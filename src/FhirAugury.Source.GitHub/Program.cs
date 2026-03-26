using Fhiraugury;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Configuration;
using FhirAugury.Common.Database;
using FhirAugury.Source.GitHub.Api;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Indexing;
using FhirAugury.Source.GitHub.Ingestion;
using FhirAugury.Source.GitHub.Workers;
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
    .AddEnvironmentVariables("FHIR_AUGURY_GITHUB_");

builder.Services.Configure<GitHubServiceOptions>(builder.Configuration.GetSection(GitHubServiceOptions.SectionName));

// ── Aspire service defaults (OpenTelemetry, health checks, resilience) ──
builder.AddServiceDefaults();

// ── Kestrel ports ────────────────────────────────────────────────
IConfigurationSection portsSection = builder.Configuration.GetSection($"{GitHubServiceOptions.SectionName}:Ports");
int httpPort = portsSection.GetValue<int>("Http", 5190);
int grpcPort = portsSection.GetValue<int>("Grpc", 5191);

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
builder.Services.AddSingleton<JiraRefExtractor>();
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
builder.Services.AddSingleton<JiraRefResolver>();

// Orchestrator client (optional — for ingestion notifications)
#pragma warning disable CS8634, CS8621 // Nullable type as generic type argument
builder.Services.AddSingleton(sp =>
{
    GitHubServiceOptions opts = sp.GetRequiredService<IOptions<GitHubServiceOptions>>().Value;
    if (string.IsNullOrWhiteSpace(opts.OrchestratorGrpcAddress))
        return (OrchestratorService.OrchestratorServiceClient?)null;
    GrpcChannel channel = GrpcChannel.ForAddress(opts.OrchestratorGrpcAddress);
    return (OrchestratorService.OrchestratorServiceClient?)new OrchestratorService.OrchestratorServiceClient(channel);
});
#pragma warning restore CS8634, CS8621

builder.Services.AddSingleton<GitHubIngestionPipeline>();
builder.Services.AddSingleton<FhirAugury.Common.Ingestion.IngestionWorkQueue>();

// Background worker
builder.Services.AddHostedService<ScheduledIngestionWorker>();

WebApplication app = builder.Build();

GitHubServiceOptions githubOpts = app.Services.GetRequiredService<IOptions<GitHubServiceOptions>>().Value;

// ── Health check ─────────────────────────────────────────────────
app.MapDefaultEndpoints();

// ── gRPC services ────────────────────────────────────────────────
app.MapGrpcService<GitHubGrpcService>();
app.MapGrpcService<GitHubSpecificGrpcService>();

// ── HTTP API ─────────────────────────────────────────────────────
app.MapGitHubHttpApi();

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

app.Run();
