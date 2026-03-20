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

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("FHIR_AUGURY_GITHUB_");

var githubOptions = new GitHubServiceOptions();
builder.Configuration.GetSection(GitHubServiceOptions.SectionName).Bind(githubOptions);

// ── Kestrel ports ────────────────────────────────────────────────
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(githubOptions.Ports.Http, o => o.Protocols = HttpProtocols.Http1AndHttp2);
    k.ListenAnyIP(githubOptions.Ports.Grpc, o => o.Protocols = HttpProtocols.Http2);
});

// ── Services ─────────────────────────────────────────────────────
builder.Services.AddGrpc();
builder.Services.AddSingleton(githubOptions);

// Database
builder.Services.AddSingleton(sp =>
{
    var dbPath = Path.GetFullPath(githubOptions.DatabasePath);
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    var db = new GitHubDatabase(dbPath, sp.GetRequiredService<ILogger<GitHubDatabase>>());
    db.Initialize();
    return db;
});

// Cache
builder.Services.AddSingleton<IResponseCache>(sp =>
{
    var cachePath = Path.GetFullPath(githubOptions.CachePath);
    Directory.CreateDirectory(cachePath);
    return new FileSystemResponseCache(cachePath);
});

// HTTP client with auth and rate limiting
builder.Services.AddHttpClient("github", client =>
{
    GitHubRateLimiter.ConfigureHttpClient(client, githubOptions);
});

// Ingestion
builder.Services.AddSingleton(sp =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpFactory.CreateClient("github");
    return new GitHubSource(
        githubOptions,
        httpClient,
        sp.GetRequiredService<GitHubDatabase>(),
        sp.GetRequiredService<IResponseCache>(),
        sp.GetRequiredService<ILogger<GitHubSource>>());
});

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
