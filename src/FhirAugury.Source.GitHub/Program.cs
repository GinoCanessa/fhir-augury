using FhirAugury.Common.Caching;
using FhirAugury.Common.Configuration;
using FhirAugury.Common.Database;
using FhirAugury.Source.GitHub.Api;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Indexing;
using FhirAugury.Source.GitHub.Ingestion;
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
}).AddHttpMessageHandler<GitHubRateLimiter>();

// Ingestion
builder.Services.AddSingleton<GitHubSource>();

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
builder.Services.AddSingleton<GitHubIngestionPipeline>();
builder.Services.AddSingleton<FhirAugury.Common.Ingestion.IngestionWorkQueue>();

// Background worker
builder.Services.AddHostedService<ScheduledIngestionWorker>();

WebApplication app = builder.Build();

// ── Ensure dictionary database ───────────────────────────────────
{
    GitHubServiceOptions opts = app.Services.GetRequiredService<IOptions<GitHubServiceOptions>>().Value;
    await FhirAugury.Common.Database.DictionaryDatabase.EnsureCreatedAsync(
        opts.DictionaryDatabase,
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DictionaryDatabase"),
        CancellationToken.None);
}

// ── Health check ─────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "github", version = "2.0.0" }));

// ── gRPC services ────────────────────────────────────────────────
app.MapGrpcService<GitHubGrpcService>();
app.MapGrpcService<GitHubSpecificGrpcService>();

// ── HTTP API ─────────────────────────────────────────────────────
app.MapGitHubHttpApi();

app.Run();
