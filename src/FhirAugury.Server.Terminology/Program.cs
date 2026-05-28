using FhirAugury.Common.OpenApi;
using FhirAugury.Server.Terminology.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

WebApplication app = builder.Build();

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
