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

// ── gRPC client ──────────────────────────────────────────────────
builder.Services.AddSingleton<OrchestratorClient>();

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
