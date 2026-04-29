using FhirAugury.Common.OpenApi;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Jira.Common.Api;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processing.Jira.Common.Hosting;
using FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
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

builder.Services.AddJiraProcessing(
    builder.Configuration,
    PreparerJiraProcessingDefaults.Apply,
    new JiraProcessingFilterDefaults { TicketStatusesToProcess = ["Triaged"] });

builder.Services.AddOptions<JiraProcessingOptions>()
    .Validate(options => !PreparerJiraProcessingDefaults.Validate(options).Any(), "Processing:Jira configuration is invalid for the preparer.")
    .ValidateOnStart();

builder.Services.AddSingleton(sp =>
{
    PreparerServiceOptions options = sp.GetRequiredService<IOptions<PreparerServiceOptions>>().Value;
    string dbPath = Path.GetFullPath(options.DatabasePath);
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    PreparerDatabase database = new(dbPath, sp.GetRequiredService<ILogger<PreparerDatabase>>());
    database.Initialize();
    return database;
});
builder.Services.AddSingleton<ProcessingDatabase>(sp => sp.GetRequiredService<PreparerDatabase>());

WebApplication app = builder.Build();

app.MapDefaultEndpoints();
app.MapProcessingEndpoints<JiraProcessingSourceTicketRecord>();
app.MapJiraProcessingTicketEndpoints();
app.MapControllers();
app.MapAuguryOpenApi();

app.Run();

public partial class Program;
