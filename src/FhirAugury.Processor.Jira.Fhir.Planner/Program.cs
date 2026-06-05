using FhirAugury.Common.OpenApi;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Jira.Common.Api;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processing.Jira.Common.Hosting;
using FhirAugury.Processing.Jira.Common.Agent;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Planner.Configuration;
using FhirAugury.Processor.Jira.Fhir.Planner.Hosting;
using FhirAugury.Processor.Jira.Fhir.Planner.Hydration;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Processing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("FHIR_AUGURY_PROCESSOR_JIRA_FHIR_PLANNER_");

builder.AddServiceDefaults();

IConfigurationSection portsSection = builder.Configuration.GetSection($"{PlannerServiceOptions.SectionName}:Ports");
int httpPort = portsSection.GetValue<int>("Http", 5172);
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(httpPort, o => o.Protocols = HttpProtocols.Http1AndHttp2);
});

builder.Services.AddControllers();
builder.Services.AddAuguryOpenApi(o =>
{
    o.Title = "FHIR Augury Processor: Jira FHIR Planner";
    o.Description = "Jira FHIR ticket planning processor and structured plan persistence service.";
});

builder.Services.AddOptions<PlannerServiceOptions>()
    .Bind(builder.Configuration.GetSection(PlannerServiceOptions.SectionName))
    .Validate(options => !options.Validate().Any(), "Processing configuration is invalid.")
    .ValidateOnStart();

builder.Services.AddOptions<PlannerOptions>()
    .Bind(builder.Configuration.GetSection(PlannerOptions.SectionName))
    .Validate(options => !PlannerRepoFilters.Validate(options).Any(), "Processing:Planner configuration is invalid.")
    .ValidateOnStart();

// Register the hydration sweeper hosted service BEFORE AddJiraProcessing so it
// starts before the processing queue worker (host invokes hosted services in
// registration order).
builder.Services.AddHostedService<FhirAugury.Processor.Jira.Fhir.Planner.Hosting.HydrationSweeperHostedService>();

builder.Services.AddJiraProcessing(
    builder.Configuration,
    PlannerJiraProcessingDefaults.Apply,
    new JiraProcessingFilterDefaults { TicketStatusesToProcess = ["Resolved - change required"] });

builder.Services.AddOptions<JiraProcessingOptions>()
    .Validate(options => !PlannerJiraProcessingDefaults.Validate(options).Any(), "Processing:Jira configuration is invalid for the planner.")
    .ValidateOnStart();

builder.Services.AddSingleton<IJiraAgentExtensionTokenProvider, PlannerAgentCommandTokenProvider>();
builder.Services.AddSingleton<IProcessingWorkItemHandler<JiraProcessingSourceTicketRecord>, PlannerTicketHandler>();

builder.Services.AddHttpClient<PlannedTicketHydrator>((sp, client) =>
{
    PlannerServiceOptions plannerOptions = sp.GetRequiredService<IOptions<PlannerServiceOptions>>().Value;
    JiraProcessingOptions jiraOptions = sp.GetRequiredService<IOptions<JiraProcessingOptions>>().Value;
    string address = !string.IsNullOrWhiteSpace(plannerOptions.OrchestratorAddress)
        ? plannerOptions.OrchestratorAddress
        : !string.IsNullOrWhiteSpace(jiraOptions.OrchestratorAddress)
            ? jiraOptions.OrchestratorAddress
            : jiraOptions.JiraSourceAddress;
    if (string.IsNullOrWhiteSpace(address))
    {
        address = "http://localhost";
    }

    client.BaseAddress = new Uri(address.EndsWith('/') ? address : address + "/");
});

builder.Services.AddHttpClient<SpecificationBackfillService>((sp, client) =>
{
    JiraProcessingOptions jiraOptions = sp.GetRequiredService<IOptions<JiraProcessingOptions>>().Value;
    string address = jiraOptions.JiraSourceAddress;
    if (string.IsNullOrWhiteSpace(address))
    {
        address = PlannerJiraProcessingDefaults.JiraSourceAddress;
    }

    client.BaseAddress = new Uri(address.EndsWith('/') ? address : address + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddOptions<HydrationOptions>()
    .Bind(builder.Configuration.GetSection($"{PlannerServiceOptions.SectionName}:Hydration"))
    .Validate(options => !options.Validate().Any(), "Processing:Hydration configuration is invalid.")
    .ValidateOnStart();
builder.Services.AddSingleton<PlannedHydrationSweeper>();

builder.Services.AddSingleton(sp =>
{
    PlannerServiceOptions options = sp.GetRequiredService<IOptions<PlannerServiceOptions>>().Value;
    string dbPath = Path.GetFullPath(options.DatabasePath);
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    PlannerDatabase database = new(dbPath, sp.GetRequiredService<ILogger<PlannerDatabase>>());
    database.Initialize();
    return database;
});
builder.Services.AddSingleton<ProcessingDatabase>(sp => sp.GetRequiredService<PlannerDatabase>());

WebApplication app = builder.Build();

app.MapDefaultEndpoints();
app.MapProcessingEndpoints<JiraProcessingSourceTicketRecord>();
app.MapJiraProcessingTicketEndpoints();
app.MapControllers();
app.MapAuguryOpenApi();

app.Run();

public partial class Program;
