using FhirAugury.Common.Caching;
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

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("FHIR_AUGURY_GITHUB_");

builder.Services.Configure<GitHubServiceOptions>(builder.Configuration.GetSection(GitHubServiceOptions.SectionName));

// ── Kestrel ports ────────────────────────────────────────────────
var portsSection = builder.Configuration.GetSection($"{GitHubServiceOptions.SectionName}:Ports");
var httpPort = portsSection.GetValue<int>("Http", 5190);
var grpcPort = portsSection.GetValue<int>("Grpc", 5191);

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
    var options = sp.GetRequiredService<IOptions<GitHubServiceOptions>>().Value;
    var dbPath = Path.GetFullPath(options.DatabasePath);
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    var db = new GitHubDatabase(dbPath, sp.GetRequiredService<ILogger<GitHubDatabase>>());
    db.Initialize();
    return db;
});

// Cache
builder.Services.AddSingleton<IResponseCache>(sp =>
{
    var options = sp.GetRequiredService<IOptions<GitHubServiceOptions>>().Value;
    var cachePath = Path.GetFullPath(options.CachePath);
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
builder.Services.AddSingleton<GitHubIndexer>();
builder.Services.AddSingleton<ArtifactFileMapper>();
builder.Services.AddSingleton<JiraRefResolver>();
builder.Services.AddSingleton<GitHubIngestionPipeline>();

// Background worker
builder.Services.AddHostedService<ScheduledIngestionWorker>();

var app = builder.Build();

// ── Health check ─────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "github", version = "2.0.0" }));

// ── gRPC services ────────────────────────────────────────────────
app.MapGrpcService<GitHubGrpcService>();
app.MapGrpcService<GitHubSpecificGrpcService>();

// ── HTTP API ─────────────────────────────────────────────────────
app.MapGitHubHttpApi();

app.Run();
