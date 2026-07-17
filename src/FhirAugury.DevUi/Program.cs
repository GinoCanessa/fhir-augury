using FhirAugury.DevUi.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("FHIR_AUGURY_DEVUI_");

// ── Aspire service defaults (OpenTelemetry, health checks, resilience) ──
builder.AddServiceDefaults();

// ── Blazor ───────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ── HTTP clients ─────────────────────────────────────────────────
builder.Services.AddHttpClient("orchestrator");
builder.Services.AddHttpClient("source-direct");
builder.Services.AddSingleton<OrchestratorClient>();
builder.Services.AddSingleton<SourceDirectClient>();
builder.Services.AddSingleton<ApiInvoker>();
builder.Services.AddSingleton<OpenApiCatalogClient>();

WebApplication app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapDefaultEndpoints();
app.MapRazorComponents<FhirAugury.DevUi.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
