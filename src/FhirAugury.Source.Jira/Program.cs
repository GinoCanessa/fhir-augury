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
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("FHIR_AUGURY_JIRA_");

builder.Services.Configure<JiraServiceOptions>(builder.Configuration.GetSection(JiraServiceOptions.SectionName));

// ── Kestrel ports ────────────────────────────────────────────────
var portsSection = builder.Configuration.GetSection($"{JiraServiceOptions.SectionName}:Ports");
var httpPort = portsSection.GetValue<int>("Http", 5160);
var grpcPort = portsSection.GetValue<int>("Grpc", 5161);

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
    var jiraOptions = sp.GetRequiredService<IOptions<JiraServiceOptions>>().Value;
    var dbPath = Path.GetFullPath(jiraOptions.DatabasePath);
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    var db = new JiraDatabase(dbPath, sp.GetRequiredService<ILogger<JiraDatabase>>());
    db.Initialize();
    return db;
});

// Cache
builder.Services.AddSingleton<IResponseCache>(sp =>
{
    var jiraOptions = sp.GetRequiredService<IOptions<JiraServiceOptions>>().Value;
    var cachePath = Path.GetFullPath(jiraOptions.CachePath);
    Directory.CreateDirectory(cachePath);
    return new FileSystemResponseCache(cachePath);
});

// HTTP client with auth
builder.Services.AddTransient<JiraAuthHandler>();
builder.Services.AddHttpClient("jira", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
    client.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/json");
    client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "FhirAugury/2.0");
}).AddHttpMessageHandler<JiraAuthHandler>();

// Ingestion
builder.Services.AddSingleton<JiraSource>();
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
