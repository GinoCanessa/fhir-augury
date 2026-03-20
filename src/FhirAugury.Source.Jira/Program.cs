using FhirAugury.Common.Caching;
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

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("FHIR_AUGURY_JIRA_");

var jiraOptions = new JiraServiceOptions();
builder.Configuration.GetSection(JiraServiceOptions.SectionName).Bind(jiraOptions);

// ── Kestrel ports ────────────────────────────────────────────────
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(jiraOptions.Ports.Http, o => o.Protocols = HttpProtocols.Http1AndHttp2);
    k.ListenAnyIP(jiraOptions.Ports.Grpc, o => o.Protocols = HttpProtocols.Http2);
});

// ── Services ─────────────────────────────────────────────────────
builder.Services.AddGrpc();
builder.Services.AddSingleton(jiraOptions);

// Database
builder.Services.AddSingleton(sp =>
{
    var dbPath = Path.GetFullPath(jiraOptions.DatabasePath);
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    var db = new JiraDatabase(dbPath, sp.GetRequiredService<ILogger<JiraDatabase>>());
    db.Initialize();
    return db;
});

// Cache
builder.Services.AddSingleton<IResponseCache>(sp =>
{
    var cachePath = Path.GetFullPath(jiraOptions.CachePath);
    Directory.CreateDirectory(cachePath);
    return new FileSystemResponseCache(cachePath);
});

// HTTP client with auth
builder.Services.AddHttpClient("jira", client =>
{
    JiraAuthHandler.ConfigureHttpClient(client, jiraOptions);
});

// Ingestion
builder.Services.AddSingleton(sp =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpFactory.CreateClient("jira");
    return new JiraSource(
        jiraOptions,
        httpClient,
        sp.GetRequiredService<JiraDatabase>(),
        sp.GetRequiredService<IResponseCache>(),
        sp.GetRequiredService<ILogger<JiraSource>>());
});

builder.Services.AddSingleton<JiraIndexer>();
builder.Services.AddSingleton<JiraIngestionPipeline>();

// Background worker
builder.Services.AddHostedService<ScheduledIngestionWorker>();

var app = builder.Build();

// ── Health check ─────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "jira", version = "2.0.0" }));

// ── gRPC services ────────────────────────────────────────────────
app.MapGrpcService<JiraGrpcService>();
app.MapGrpcService<JiraSpecificGrpcService>();

// ── HTTP API ─────────────────────────────────────────────────────
app.MapJiraHttpApi();

app.Run();
