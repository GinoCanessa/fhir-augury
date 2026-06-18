using FhirAugury.Common.Indexing;
using FhirAugury.Common.OpenApi;
using FhirAugury.Source.Fhir.Configuration;
using FhirAugury.Source.Fhir.Database;
using FhirAugury.Source.Fhir.Readers;
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
    .AddEnvironmentVariables("FHIR_AUGURY_FHIR_");

builder.Services.Configure<FhirServiceOptions>(builder.Configuration.GetSection(FhirServiceOptions.SectionName));

// ── Aspire service defaults (OpenTelemetry, health checks, resilience) ──
builder.AddServiceDefaults();

// ── Kestrel ports ────────────────────────────────────────────────
IConfigurationSection portsSection = builder.Configuration.GetSection($"{FhirServiceOptions.SectionName}:Ports");
int httpPort = portsSection.GetValue<int>("Http", 5195);

builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(httpPort, o => o.Protocols = HttpProtocols.Http1AndHttp2);
});

// ── Services ─────────────────────────────────────────────────────
builder.Services.AddControllers();

builder.Services.AddAuguryOpenApi(o =>
{
    o.Title = "FHIR Augury Source: Fhir";
    o.Description = "FHIR specification source service — a read-only query surface over the parsed spec database.";
});

// Read-only spec database (no schema init — the file is produced upstream).
builder.Services.AddSingleton(sp =>
{
    FhirServiceOptions options = sp.GetRequiredService<IOptions<FhirServiceOptions>>().Value;
    string dbPath = Path.GetFullPath(options.DatabasePath);
    return new FhirSpecDatabase(dbPath, sp.GetRequiredService<ILogger<FhirSpecDatabase>>());
});

builder.Services.AddSingleton(sp =>
{
    FhirServiceOptions options = sp.GetRequiredService<IOptions<FhirServiceOptions>>().Value;
    return new FhirReleaseResolver(sp.GetRequiredService<FhirSpecDatabase>(), options.DefaultRelease);
});

builder.Services.AddSingleton<FhirSpecReader>();

// Index tracker (Phase 5 registers the FTS index with it).
IndexTracker indexTracker = new();
builder.Services.AddSingleton<IIndexTracker>(indexTracker);
builder.Services.AddSingleton(indexTracker);

WebApplication app = builder.Build();

// ── Health check ─────────────────────────────────────────────────
app.MapDefaultEndpoints();

// ── HTTP API ─────────────────────────────────────────────────────
app.MapControllers();
app.MapAuguryOpenApi();

app.Run();
