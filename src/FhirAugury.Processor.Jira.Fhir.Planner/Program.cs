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
using FhirAugury.Processor.Jira.Fhir.Planner.Configuration;
using FhirAugury.Processor.Jira.Fhir.Planner.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Processing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("FHIR_AUGURY_PROCESSOR_JIRA_FHIR_PLANNER_");

builder.AddServiceDefaults();

IConfigurationSection portsSection = builder.Configuration.GetSection($"{ProcessingServiceOptions.SectionName}:Ports");
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

builder.Services.AddOptions<PlannerOptions>()
    .Bind(builder.Configuration.GetSection(PlannerOptions.SectionName))
    .Validate(options => !PlannerRepoFilters.Validate(options).Any(), "Processing:Planner configuration is invalid.")
    .ValidateOnStart();

builder.Services.AddJiraProcessing(
    builder.Configuration,
    PlannerJiraProcessingDefaults.Apply,
    new JiraProcessingFilterDefaults { TicketStatusesToProcess = ["Resolved - change required"] });

builder.Services.AddOptions<JiraProcessingOptions>()
    .Validate(options => !PlannerJiraProcessingDefaults.Validate(options).Any(), "Processing:Jira configuration is invalid for the planner.")
    .ValidateOnStart();

builder.Services.AddSingleton<IJiraAgentExtensionTokenProvider, PlannerAgentCommandTokenProvider>();
builder.Services.AddSingleton<IProcessingWorkItemHandler<JiraProcessingSourceTicketRecord>, PlannerTicketHandler>();

builder.Services.AddSingleton(sp =>
{
    ProcessingServiceOptions options = sp.GetRequiredService<IOptions<ProcessingServiceOptions>>().Value;
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
