using FhirAugury.Common.Caching;
using FhirAugury.Common.Configuration;
using FhirAugury.Common.Database;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Indexing;
using FhirAugury.Source.GitHub.Ingestion;
using FhirAugury.Source.GitHub.Ingestion.Categories;
using FhirAugury.Source.GitHub.Workers;
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
    .AddEnvironmentVariables("FHIR_AUGURY_GITHUB_");

builder.Services.Configure<GitHubServiceOptions>(builder.Configuration.GetSection(GitHubServiceOptions.SectionName));

// ── Aspire service defaults (OpenTelemetry, health checks, resilience) ──
builder.AddServiceDefaults();

// ── Kestrel ports ────────────────────────────────────────────────
IConfigurationSection portsSection = builder.Configuration.GetSection($"{GitHubServiceOptions.SectionName}:Ports");
int httpPort = portsSection.GetValue<int>("Http", 5190);

builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(httpPort, o => o.Protocols = HttpProtocols.Http1AndHttp2);
});

// ── Services ─────────────────────────────────────────────────────

builder.Services.AddControllers();

// Database
builder.Services.AddSingleton(sp =>
{
    GitHubServiceOptions options = sp.GetRequiredService<IOptions<GitHubServiceOptions>>().Value;
    string dbPath = Path.GetFullPath(options.DatabasePath);
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    GitHubDatabase db = new GitHubDatabase(dbPath, sp.GetRequiredService<ILogger<GitHubDatabase>>(), ftsTokenizer: options.Bm25.FtsTokenizer);
    db.Initialize();
    return db;
});

// Cache
builder.Services.AddSingleton<IResponseCache>(sp =>
{
    GitHubServiceOptions options = sp.GetRequiredService<IOptions<GitHubServiceOptions>>().Value;
    string cachePath = Path.GetFullPath(options.CachePath);
    Directory.CreateDirectory(cachePath);
    return new FileSystemResponseCache(cachePath);
});

// HTTP client with auth and rate limiting
builder.Services.AddTransient<GitHubRateLimiter>();
builder.Services.AddHttpClient("github", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
    client.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/vnd.github+json");
    client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "FhirAugury/2.0");
    client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
}).AddHttpMessageHandler<GitHubRateLimiter>()
  .AddStandardResilienceHandler();

// Ingestion
// Ingestion — provider selection based on config
builder.Services.AddSingleton<GhCliRunner>();
builder.Services.AddSingleton<IGitHubDataProvider>(sp =>
{
    GitHubServiceOptions options = sp.GetRequiredService<IOptions<GitHubServiceOptions>>().Value;
    return options.Provider.ToLowerInvariant() switch
    {
        "gh-cli" => ActivatorUtilities.CreateInstance<GitHubCliProvider>(sp),
        _ => ActivatorUtilities.CreateInstance<GitHubRestProvider>(sp),
    };
});

builder.Services.AddSingleton<GitHubRepoCloner>();
builder.Services.AddSingleton<GitHubCommitFileExtractor>();
builder.Services.AddSingleton<GitHubFileContentIndexer>();
builder.Services.AddSingleton<GitHubXRefRebuilder>();
builder.Services.AddSingleton(sp =>
{
    GitHubServiceOptions opts = sp.GetRequiredService<IOptions<GitHubServiceOptions>>().Value;
    return new AuxiliaryDatabase(opts.AuxiliaryDatabase, sp.GetRequiredService<ILogger<AuxiliaryDatabase>>());
});
builder.Services.AddSingleton(sp =>
{
    GitHubServiceOptions opts = sp.GetRequiredService<IOptions<GitHubServiceOptions>>().Value;
    return new GitHubIndexer(
        sp.GetRequiredService<GitHubDatabase>(),
        sp.GetRequiredService<AuxiliaryDatabase>(),
        opts.Bm25,
        sp.GetRequiredService<ILogger<GitHubIndexer>>());
});
builder.Services.AddSingleton<ArtifactFileMapper>();
builder.Services.AddSingleton<CanonicalArtifactIndexer>();
builder.Services.AddSingleton<StructureDefinitionIndexer>();
builder.Services.AddSingleton<FshArtifactIndexer>();

// File tagging
builder.Services.Configure<TagWeightOptions>(builder.Configuration.GetSection("GitHub:TagWeights"));
builder.Services.AddSingleton<TagWeightResolver>();
builder.Services.AddSingleton<IRepoCategoryStrategy, FhirCoreStrategy>();
builder.Services.AddSingleton<IRepoCategoryStrategy, UtgStrategy>();
builder.Services.AddSingleton<IRepoCategoryStrategy, FhirExtensionsPackStrategy>();
builder.Services.AddSingleton<IRepoCategoryStrategy, IncubatorStrategy>();
builder.Services.AddSingleton<IRepoCategoryStrategy, IgStrategy>();

// Index tracker
FhirAugury.Common.Indexing.IndexTracker indexTracker = new();
builder.Services.AddSingleton<FhirAugury.Common.Indexing.IIndexTracker>(indexTracker);
builder.Services.AddSingleton(indexTracker);

// Orchestrator HTTP client (optional — for ingestion notifications)
{
    GitHubServiceOptions opts = builder.Configuration.GetSection(GitHubServiceOptions.SectionName).Get<GitHubServiceOptions>()!;
    if (!string.IsNullOrWhiteSpace(opts.OrchestratorAddress))
    {
        builder.Services.AddHttpClient("orchestrator", client =>
        {
            client.BaseAddress = new Uri(opts.OrchestratorAddress);
        });
    }
}

builder.Services.AddSingleton<GitHubIngestionPipeline>();
builder.Services.AddSingleton<FhirAugury.Common.Ingestion.IngestionWorkQueue>();

// Background worker
builder.Services.AddHostedService<ScheduledIngestionWorker>();

WebApplication app = builder.Build();

GitHubServiceOptions githubOpts = app.Services.GetRequiredService<IOptions<GitHubServiceOptions>>().Value;

// Register indexes with the tracker
FhirAugury.Common.Indexing.IndexTracker tracker = app.Services.GetRequiredService<FhirAugury.Common.Indexing.IndexTracker>();
GitHubDatabase githubDatabase = app.Services.GetRequiredService<GitHubDatabase>();
tracker.RegisterIndex("bm25", "BM25 keyword scoring index", () =>
{
    using Microsoft.Data.Sqlite.SqliteConnection c = githubDatabase.OpenConnection();
    using Microsoft.Data.Sqlite.SqliteCommand cmd = new("SELECT COUNT(*) FROM index_keywords", c);
    return Convert.ToInt32(cmd.ExecuteScalar());
});
tracker.RegisterIndex("fts", "FTS5 full-text search index", () =>
{
    using Microsoft.Data.Sqlite.SqliteConnection c = githubDatabase.OpenConnection();
    return FhirAugury.Source.GitHub.Database.Records.GitHubIssueRecord.SelectCount(c);
});
tracker.RegisterIndex("cross-refs", "Cross-reference extraction", () =>
{
    using Microsoft.Data.Sqlite.SqliteConnection c = githubDatabase.OpenConnection();
    return FhirAugury.Common.Database.Records.JiraXRefRecord.SelectCount(c);
});
tracker.RegisterIndex("commits", "Git commit extraction", () =>
{
    using Microsoft.Data.Sqlite.SqliteConnection c = githubDatabase.OpenConnection();
    return FhirAugury.Source.GitHub.Database.Records.GitHubCommitRecord.SelectCount(c);
});
tracker.RegisterIndex("artifact-map", "FHIR artifact-to-file mapping", () =>
{
    using Microsoft.Data.Sqlite.SqliteConnection c = githubDatabase.OpenConnection();
    return FhirAugury.Source.GitHub.Database.Records.GitHubSpecFileMapRecord.SelectCount(c);
});
tracker.RegisterIndex("file-contents", "Repository file content indexing", () =>
{
    using Microsoft.Data.Sqlite.SqliteConnection c = githubDatabase.OpenConnection();
    return FhirAugury.Source.GitHub.Database.Records.GitHubFileContentRecord.SelectCount(c);
});

// ── Health check ─────────────────────────────────────────────────
app.MapDefaultEndpoints();

// ── HTTP API ─────────────────────────────────────────────────────
app.MapControllers();

// ── Ensure dictionary database ───────────────────────────────────
await FhirAugury.Common.Database.DictionaryDatabase.EnsureCreatedAsync(
    githubOpts.DictionaryDatabase,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DictionaryDatabase"),
    CancellationToken.None);

// ── Validate gh CLI if selected ──────────────────────────────────
if (githubOpts.Provider.Equals("gh-cli", StringComparison.OrdinalIgnoreCase))
{
    GhCliRunner ghRunner = app.Services.GetRequiredService<GhCliRunner>();
    bool valid = await ghRunner.ValidateAsync(CancellationToken.None);
    if (!valid)
    {
        ILogger startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
        startupLogger.LogCritical(
            "gh CLI provider selected but gh is not available or not authenticated. " +
            "Run 'gh auth login' or set Provider to 'rest' in configuration.");
        throw new InvalidOperationException("gh CLI is not available or not authenticated.");
    }
}

// ── Reload from cache on startup (if configured) ─────────────────
if (githubOpts.ReloadFromCacheOnStartup ||
    app.Services.GetRequiredService<GitHubDatabase>().PrimaryContentTableIsEmpty())
{
    GitHubIngestionPipeline pipeline = app.Services.GetRequiredService<GitHubIngestionPipeline>();
    await pipeline.RebuildFromCacheAsync(CancellationToken.None);
}
else
{
    // Check individual index tables when not reloading from cache
    GitHubDatabase githubDb = app.Services.GetRequiredService<GitHubDatabase>();
    ILogger startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    if (githubDb.TableIsEmpty("xref_jira"))
    {
        startupLogger.LogInformation("Cross-reference indexes are empty — rebuilding");
        GitHubXRefRebuilder xrefRebuilder = app.Services.GetRequiredService<GitHubXRefRebuilder>();
        List<string> repos = githubOpts.GetAllRepositoryNames();
        xrefRebuilder.RebuildAllRepos(repos, validJiraNumbers: null, CancellationToken.None);
    }

    if (githubDb.TableIsEmpty("index_keywords"))
    {
        startupLogger.LogInformation("BM25 index is empty — rebuilding");
        app.Services.GetRequiredService<GitHubIndexer>().RebuildFullIndex(CancellationToken.None);
    }
}

app.Run();
