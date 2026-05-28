using FhirAugury.Common.OpenApi;
using FhirAugury.Server.Terminology.Configuration;
using FhirAugury.Server.Terminology.Database;
using FhirAugury.Server.Terminology.Hosting;
using FhirAugury.Server.Terminology.Ingestion;
using FhirAugury.Server.Terminology.Matching;
using FhirPkg;
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
    .AddEnvironmentVariables("FHIR_AUGURY_SERVER_TERMINOLOGY_");

builder.Services.Configure<TerminologyServiceOptions>(
    builder.Configuration.GetSection(TerminologyServiceOptions.SectionName));

// ── Aspire service defaults (OpenTelemetry, health checks, resilience) ──
builder.AddServiceDefaults();

// ── Kestrel ports ────────────────────────────────────────────────
IConfigurationSection portsSection = builder.Configuration
    .GetSection($"{TerminologyServiceOptions.SectionName}:Ports");
int httpPort = portsSection.GetValue<int>("Http", 5300);

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
    o.Title = "FHIR Augury Server: Terminology";
    o.Description = "THO overlap check for submitted CodeSystem / ValueSet resources.";
});

// Database
builder.Services.AddSingleton(sp =>
{
    TerminologyServiceOptions opts = sp.GetRequiredService<IOptions<TerminologyServiceOptions>>().Value;
    string dbPath = Path.GetFullPath(opts.DatabasePath);
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    TerminologyDatabase db = new TerminologyDatabase(
        dbPath,
        sp.GetRequiredService<ILogger<TerminologyDatabase>>());
    db.Initialize();
    return db;
});

// FhirPkg (THO package acquisition)
{
    TerminologyServiceOptions bootstrap = builder.Configuration
        .GetSection(TerminologyServiceOptions.SectionName)
        .Get<TerminologyServiceOptions>() ?? new TerminologyServiceOptions();

    string cachePath = Path.GetFullPath(bootstrap.CachePath);
    Directory.CreateDirectory(cachePath);

    builder.Services.AddFhirPackageManagement(o =>
    {
        FhirPackageSource.ApplyOptions(o, bootstrap);
    });
}

builder.Services.AddSingleton<TerminologyResourceParser>();
builder.Services.AddSingleton<FhirPackageSource>();
builder.Services.AddSingleton<TerminologyArtifactNormalizer>();
builder.Services.AddSingleton<TerminologyIngestionPipeline>();
builder.Services.AddSingleton<TerminologyIndexStatusTracker>();

// Matching pipeline (Phase 3+)
builder.Services.AddSingleton<SubmissionNormalizer>();
builder.Services.AddSingleton<LexicalMatcher>();
builder.Services.AddSingleton<ITerminologyMatcher>(sp => sp.GetRequiredService<LexicalMatcher>());

// Startup rebuild — runs after Kestrel binds.
builder.Services.AddSingleton<TerminologyStartupRebuildService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TerminologyStartupRebuildService>());
builder.Services.AddSingleton<FhirAugury.Common.Hosting.IStartupRebuildStatus>(
    sp => sp.GetRequiredService<TerminologyStartupRebuildService>());

WebApplication app = builder.Build();
TerminologyServiceOptions termOpts = app.Services.GetRequiredService<IOptions<TerminologyServiceOptions>>().Value;

// ── Validate project config ─────────────────────────────────────
{
    List<string> validationErrors = termOpts.Validate().ToList();
    if (validationErrors.Count > 0)
    {
        throw new InvalidOperationException(
            "Invalid Terminology service configuration:" + Environment.NewLine +
            string.Join(Environment.NewLine, validationErrors));
    }
}

// ── Health check ─────────────────────────────────────────────────
app.MapDefaultEndpoints();

// ── HTTP API ─────────────────────────────────────────────────────
app.MapControllers();
app.MapAuguryOpenApi();

app.Run();

/// <summary>
/// Public partial Program class so tests can use
/// <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program;
