using FhirAugury.DevUi.Configuration;
using FhirAugury.DevUi.Services;
using FhirAugury.Processing.Client;
using FhirAugury.Publishing.Tickets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

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
builder.Services.AddDevUiOperations(
    builder.Configuration,
    builder.Environment);

WebApplication app = builder.Build();
ReviewSiteStore reviewSiteStore =
    app.Services.GetRequiredService<ReviewSiteStore>();
reviewSiteStore.EnsureRoots();
ReviewSiteFileProvider reviewSitesFileProvider =
    new(reviewSiteStore);
app.Lifetime.ApplicationStopped.Register(
    reviewSitesFileProvider.Dispose);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.Map("/review-sites", reviewSites =>
{
    reviewSites.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = reviewSitesFileProvider,
    });
    reviewSites.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = reviewSitesFileProvider,
    });
});
app.UseStaticFiles();
app.UseAntiforgery();

app.MapDefaultEndpoints();
app.MapRazorComponents<FhirAugury.DevUi.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();

public static class DevUiServiceCollectionExtensions
{
    public static IServiceCollection AddDevUiOperations(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddSingleton<IFileSystemInspector,
            PhysicalFileSystemInspector>();
        services.AddSingleton<IValidateOptions<DevUiOptions>>(provider =>
            new DevUiOptionsValidator(
                environment,
                provider.GetRequiredService<IFileSystemInspector>()));
        services.AddOptions<DevUiOptions>()
            .Bind(configuration.GetSection(
                DevUiOptions.SectionName))
            .ValidateOnStart();

        services.AddHttpClient(
            "orchestrator",
            (provider, client) =>
            {
                client.BaseAddress =
                    GetOrchestratorBaseAddress(provider);
            });
        services.AddHttpClient<
            IAuthoringControlClient,
            AuthoringControlClient>(
            (provider, client) =>
            {
                client.BaseAddress =
                    GetOrchestratorBaseAddress(provider);
            });
        services.AddHttpClient<
            IOrchestratorReadinessClient,
            OrchestratorReadinessClient>(
            (provider, client) =>
            {
                client.BaseAddress =
                    GetOrchestratorBaseAddress(provider);
            });

        services.AddSingleton<OrchestratorClient>();
        services.AddSingleton<ApiInvoker>();
        services.AddSingleton<OpenApiCatalogClient>();
        services.AddSingleton<TicketWorkflowCatalog>();
        services.AddSingleton<JiraTicketKeyParser>();
        services.AddSingleton<ReadinessEvaluator>();
        services.AddSingleton<RunOutcomeClassifier>();
        services.AddSingleton<ITicketSitePublisher,
            TicketSitePublisher>();
        services.AddSingleton<ReviewSiteStore>();
        services.AddSingleton<IReviewSiteStore>(provider =>
            provider.GetRequiredService<ReviewSiteStore>());
        services.AddSingleton(TimeProvider.System);
        services.AddTransient<RunPollingSession>();
        services.AddScoped<TicketOperationsService>();
        return services;
    }

    private static Uri GetOrchestratorBaseAddress(
        IServiceProvider provider)
    {
        string address = provider
            .GetRequiredService<IOptions<DevUiOptions>>()
            .Value
            .OrchestratorAddress;
        return new Uri(
            $"{address.TrimEnd('/')}/",
            UriKind.Absolute);
    }
}
