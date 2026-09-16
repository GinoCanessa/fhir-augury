using FhirAugury.Common.OpenApi;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Api;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processing.Jira.Common.Hosting;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Hosting;
using FhirAugury.Processor.Jira.Fhir.Preparer.Hydration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("FHIR_AUGURY_PREPARER_");

builder.AddServiceDefaults();

IConfigurationSection portsSection = builder.Configuration.GetSection($"{PreparerServiceOptions.SectionName}:Ports");
int httpPort = portsSection.GetValue<int>("Http", 5171);
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(httpPort, o => o.Protocols = HttpProtocols.Http1AndHttp2);
});

builder.Services.AddControllers();
builder.Services.AddAuguryOpenApi(o =>
{
    o.Title = "FHIR Augury Processor: Jira FHIR Preparer";
    o.Description = "Jira FHIR ticket preparation processor and prepared-result query API.";
});

builder.Services.AddOptions<PreparerServiceOptions>()
    .Bind(builder.Configuration.GetSection(PreparerServiceOptions.SectionName))
    .Validate(options => !options.Validate().Any(), "Processing configuration is invalid.")
    .ValidateOnStart();

// Register the hydration sweeper hosted service BEFORE AddJiraProcessing so it
// starts before ProcessingHostedService<JiraProcessingSourceTicketRecord> and
// JiraTicketSyncWorker. Microsoft.Extensions.Hosting honors registration order.
builder.Services.AddHostedService<FhirAugury.Processor.Jira.Fhir.Preparer.Hosting.HydrationSweeperHostedService>();

builder.Services.AddJiraProcessing(
    builder.Configuration,
    PreparerJiraProcessingDefaults.Apply,
    new JiraProcessingFilterDefaults { TicketStatusesToProcess = ["Triaged"] });

builder.Services.AddOptions<JiraProcessingOptions>()
    .Validate(options => !PreparerJiraProcessingDefaults.Validate(options).Any(), "Processing:Jira configuration is invalid for the preparer.")
    .ValidateOnStart();

builder.Services.AddSingleton<FhirTicketPrepHandler>();
builder.Services.AddSingleton<IProcessingWorkItemHandler<JiraProcessingSourceTicketRecord>>(sp =>
    sp.GetRequiredService<FhirTicketPrepHandler>());
builder.Services.AddSingleton<IAuthoringWorkItemHandler<JiraAuthoringWorkItem>>(sp =>
    sp.GetRequiredService<FhirTicketPrepHandler>());

builder.Services.AddHttpClient<PreparedTicketHydrator>((sp, client) =>
{
    ProcessingServiceOptions processingOptions = sp.GetRequiredService<IOptions<PreparerServiceOptions>>().Value;
    JiraProcessingOptions jiraOptions = sp.GetRequiredService<IOptions<JiraProcessingOptions>>().Value;
    string address = !string.IsNullOrWhiteSpace(processingOptions.OrchestratorAddress)
        ? processingOptions.OrchestratorAddress
        : !string.IsNullOrWhiteSpace(jiraOptions.OrchestratorAddress)
            ? jiraOptions.OrchestratorAddress
            : jiraOptions.JiraSourceAddress;
    if (string.IsNullOrWhiteSpace(address))
    {
        // Best-effort default; with no configured address every hydration row will
        // land as "unresolved" with an explanatory reason — see PreparedTicketHydrator.
        address = "http://localhost";
    }

    client.BaseAddress = new Uri(address.EndsWith('/') ? address : address + "/");
});
builder.Services.AddHttpClient<OrchestratorWorkGroupCatalogFetcher>((sp, client) =>
{
    ProcessingServiceOptions processingOptions =
        sp.GetRequiredService<IOptions<PreparerServiceOptions>>().Value;
    string address = string.IsNullOrWhiteSpace(processingOptions.OrchestratorAddress)
        ? "http://localhost:5150"
        : processingOptions.OrchestratorAddress;
    client.BaseAddress = new Uri(address.EndsWith('/') ? address : address + "/");
});
builder.Services.AddHttpClient(
    PreparedTicketPublicationRefreshService.HttpClientName,
    (sp, client) =>
    {
        ProcessingServiceOptions processingOptions =
            sp.GetRequiredService<IOptions<PreparerServiceOptions>>().Value;
        JiraProcessingOptions jiraOptions =
            sp.GetRequiredService<IOptions<JiraProcessingOptions>>().Value;
        string address =
            !string.IsNullOrWhiteSpace(
                processingOptions.OrchestratorAddress)
                ? processingOptions.OrchestratorAddress
                : !string.IsNullOrWhiteSpace(
                    jiraOptions.OrchestratorAddress)
                    ? jiraOptions.OrchestratorAddress
                    : "http://localhost:5150";
        client.BaseAddress = new Uri(
            address.EndsWith('/') ? address : address + "/");
    });
builder.Services.AddSingleton(sp =>
    new OrchestratorHydrationFetcher(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(
            PreparedTicketPublicationRefreshService.HttpClientName),
        sp.GetRequiredService<
            ILogger<OrchestratorHydrationFetcher>>()));

builder.Services.AddHttpClient<SpecificationBackfillService>((sp, client) =>
{
    JiraProcessingOptions jiraOptions = sp.GetRequiredService<IOptions<JiraProcessingOptions>>().Value;
    string address = jiraOptions.JiraSourceAddress;
    if (string.IsNullOrWhiteSpace(address))
    {
        address = PreparerJiraProcessingDefaults.JiraSourceAddress;
    }

    client.BaseAddress = new Uri(address.EndsWith('/') ? address : address + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddOptions<HydrationOptions>()
    .Bind(builder.Configuration.GetSection($"{PreparerServiceOptions.SectionName}:Hydration"))
    .Validate(options => !options.Validate().Any(), "Processing:Hydration configuration is invalid.")
    .ValidateOnStart();
builder.Services.AddSingleton<PreparedHydrationSweeper>();

builder.Services.AddSingleton(sp =>
{
    PreparerServiceOptions options = sp.GetRequiredService<IOptions<PreparerServiceOptions>>().Value;
    string dbPath = Path.GetFullPath(options.DatabasePath);
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    PreparerDatabase database = new(dbPath, sp.GetRequiredService<ILogger<PreparerDatabase>>());
    database.AcquireStartupOwnership();
    try
    {
        database.Initialize();
        return database;
    }
    catch
    {
        database.Dispose();
        throw;
    }
});
builder.Services.AddSingleton<ProcessingDatabase>(sp => sp.GetRequiredService<PreparerDatabase>());
builder.Services.AddSingleton<SqliteReviewSnapshotReconciler>();
builder.Services.AddSingleton<IPreparedTicketGroupingDispatcher, PreviewPreparedTicketGroupingDispatcher>();
builder.Services.AddSingleton<AuthoringRunSchedulerWakeSignal>();
builder.Services.AddSingleton<
    IPreparedTicketPublicationRefreshInterruptionHook,
    NoOpPreparedTicketPublicationRefreshInterruptionHook>();
builder.Services.AddSingleton<PreparedTicketPublicationBaselineReader>();
builder.Services.AddSingleton<PreparedTicketPublicationEnricher>();
builder.Services.AddSingleton<PreparedTicketPublicationRefreshService>();
builder.Services.AddSingleton<PreparedTicketPublicationReconciliationPlanner>();
builder.Services.AddSingleton<PreparedTicketRunWorkflowRegistry>();
builder.Services.AddSingleton<PreparedTicketSnapshotMaterializer>();
builder.Services.AddSingleton<PreparedTicketRunPostProcessor>();
builder.Services.AddSingleton<PreparedTicketGroupingMaintenanceService>();
builder.Services.AddSingleton<IAuthoringRunFinalizationStrategy>(sp =>
    sp.GetRequiredService<PreparedTicketRunPostProcessor>());
builder.Services.AddSingleton<AuthoringRunScheduler<JiraAuthoringWorkItem>>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<AuthoringRunScheduler<JiraAuthoringWorkItem>>());

WebApplication app = builder.Build();

string? configuredSnapshotSchemaVersion =
    app.Configuration[
        $"{PreparerServiceOptions.SectionName}:SnapshotSchemaVersion"];
object effectiveSnapshotSchemaVersion =
    int.TryParse(
        configuredSnapshotSchemaVersion,
        out int parsedSnapshotSchemaVersion)
        ? parsedSnapshotSchemaVersion
        : (object?)configuredSnapshotSchemaVersion ??
          new PreparerServiceOptions().SnapshotSchemaVersion;
app.Logger.LogInformation(
    "Effective Preparer snapshot schema version is {SnapshotSchemaVersion}",
    effectiveSnapshotSchemaVersion);

PreparerDatabase preparerDatabase =
    app.Services.GetRequiredService<PreparerDatabase>();
PreparerServiceOptions preparerOptions =
    app.Services.GetRequiredService<IOptions<PreparerServiceOptions>>().Value;
AuthoringRunStore preparerAuthoringStore =
    app.Services.GetRequiredService<AuthoringRunStore>();
JiraAuthoringRunCoordinator preparerRunCoordinator =
    app.Services.GetRequiredService<JiraAuthoringRunCoordinator>();
preparerDatabase.AcquireStartupOwnership();
await preparerDatabase.RecoverInterruptedMaintenanceLeasesAsync();
if (preparerOptions.ActivateRunBackedAuthoring)
{
    AuthoringCutoverCoordinator cutover =
        new(preparerDatabase.OpenConnection);
    await cutover.ActivateAsync(
        new AuthoringCutoverRequest(
            preparerRunCoordinator.ProcessorKind,
            Path.GetFullPath(preparerOptions.DatabasePath),
            Path.GetFullPath(preparerOptions.PreCutoverBackupPath!)),
        preparerDatabase);
}
else
{
    await preparerAuthoringStore.EnsureProcessorModeAsync(
        preparerRunCoordinator.ProcessorKind);
}

app.MapDefaultEndpoints();
app.MapProcessingEndpoints<JiraProcessingSourceTicketRecord>();
app.MapJiraProcessingTicketEndpoints();
app.MapControllers();
app.MapAuguryOpenApi();

app.Run();

public partial class Program;
