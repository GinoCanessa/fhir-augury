using FhirAugury.Common.OpenApi;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Jira.Common.Api;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processing.Jira.Common.Hosting;
using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Database;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("FHIR_AUGURY_PROCESSOR_JIRA_FHIR_APPLIER_");

builder.AddServiceDefaults();

IConfigurationSection portsSection = builder.Configuration.GetSection($"{ProcessingServiceOptions.SectionName}:Ports");
int httpPort = portsSection.GetValue<int>("Http", 5173);
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(httpPort, o => o.Protocols = HttpProtocols.Http1AndHttp2);
});

builder.Services.AddControllers();
builder.Services.AddAuguryOpenApi(o =>
{
    o.Title = "FHIR Augury Processor: Jira FHIR Applier";
    o.Description = "Jira FHIR ticket applier processor that drives per-(ticket, repo) agent invocations against worktrees, runs the repo build, diffs output against a baseline, and locally commits.";
});

builder.Services.AddOptions<ApplierOptions>()
    .Bind(builder.Configuration.GetSection(ApplierOptions.SectionName))
    .Validate(options => !options.Validate().Any(), "Processing:Applier configuration is invalid.")
    .ValidateOnStart();

builder.Services.AddOptions<ApplierAuthOptions>()
    .Bind(builder.Configuration.GetSection(ApplierAuthOptions.SectionName));

builder.Services.AddJiraProcessing(
    builder.Configuration,
    ApplierJiraProcessingDefaults.Apply,
    new JiraProcessingFilterDefaults
    {
        TicketStatusesToProcess = ["Resolved - change required"],
        TicketTypesToProcess = ["Change Request", "Technical Correction"],
    });

builder.Services.AddOptions<JiraProcessingOptions>()
    .Validate(options => !ApplierJiraProcessingDefaults.Validate(options).Any(), "Processing:Jira configuration is invalid for the applier.")
    .ValidateOnStart();

builder.Services.AddSingleton(sp =>
{
    ProcessingServiceOptions options = sp.GetRequiredService<IOptions<ProcessingServiceOptions>>().Value;
    string dbPath = Path.GetFullPath(options.DatabasePath);
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    ApplierDatabase database = new(dbPath, sp.GetRequiredService<ILogger<ApplierDatabase>>());
    database.Initialize();
    return database;
});
builder.Services.AddSingleton<ProcessingDatabase>(sp => sp.GetRequiredService<ApplierDatabase>());

WebApplication app = builder.Build();

app.MapDefaultEndpoints();
app.MapJiraProcessingTicketEndpoints();
app.MapControllers();
app.MapAuguryOpenApi();

app.Run();

public partial class Program;
