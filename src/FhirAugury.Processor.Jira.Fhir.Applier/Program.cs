using FhirAugury.Common.OpenApi;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Agent;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Database;
using FhirAugury.Processor.Jira.Fhir.Applier.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Applier.Processing;
using FhirAugury.Processor.Jira.Fhir.Applier.Push;
using FhirAugury.Processor.Jira.Fhir.Applier.Workspace;
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

builder.Services.AddOptions<ProcessingServiceOptions>()
    .Bind(builder.Configuration.GetSection(ProcessingServiceOptions.SectionName))
    .Validate(options => !options.Validate().Any(), "Processing configuration is invalid.")
    .ValidateOnStart();

builder.Services.AddOptions<JiraProcessingOptions>()
    .Bind(builder.Configuration.GetSection(JiraProcessingOptions.SectionName))
    .Configure(ApplierJiraProcessingDefaults.Apply)
    .Validate(options => !ApplierJiraProcessingDefaults.Validate(options).Any(), "Processing:Jira configuration is invalid for the applier.")
    .ValidateOnStart();

builder.Services.AddOptions<ApplierOptions>()
    .Bind(builder.Configuration.GetSection(ApplierOptions.SectionName))
    .Validate(options => !options.Validate().Any(), "Processing:Applier configuration is invalid.")
    .ValidateOnStart();

builder.Services.AddOptions<ApplierAuthOptions>()
    .Bind(builder.Configuration.GetSection(ApplierAuthOptions.SectionName));

builder.Services.AddSingleton<ProcessingLifecycleService>();

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

builder.Services.AddSingleton(sp =>
{
    ApplierOptions options = sp.GetRequiredService<IOptions<ApplierOptions>>().Value;
    return new PlannerReadOnlyDatabase(
        Path.GetFullPath(options.PlannerDatabasePath),
        sp.GetRequiredService<ILogger<PlannerReadOnlyDatabase>>());
});

builder.Services.AddSingleton<AppliedTicketQueueItemStore>();
builder.Services.AddSingleton<IProcessingWorkItemStore<AppliedTicketQueueItemRecord>>(sp => sp.GetRequiredService<AppliedTicketQueueItemStore>());
builder.Services.AddSingleton<AppliedTicketWriteStore>();

builder.Services.AddSingleton<RepoBaselineStore>();
builder.Services.AddSingleton<RepoLockManager>();
builder.Services.AddSingleton<IGitProcessRunner, GitProcessRunner>();
builder.Services.AddSingleton<BuildCommandRunner>();
builder.Services.AddSingleton<RepoWorkspaceManager>();
builder.Services.AddSingleton<OutputDiffer>();
builder.Services.AddSingleton<GitWorktreeCommitService>();

builder.Services.AddSingleton<JiraAgentCommandRenderer>();
builder.Services.AddSingleton<IJiraAgentCliRunner, JiraAgentCliRunner>();
builder.Services.AddSingleton<IGitPushService, GitPushService>();
builder.Services.AddSingleton<IProcessingWorkItemHandler<AppliedTicketQueueItemRecord>, ApplierTicketHandler>();
builder.Services.AddSingleton<ProcessingQueueRunner<AppliedTicketQueueItemRecord>>();
builder.Services.AddHostedService<ProcessingHostedService<AppliedTicketQueueItemRecord>>();

builder.Services.AddSingleton<PlannerWorkQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PlannerWorkQueue>());

builder.Services.AddSingleton<BaselineSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BaselineSyncService>());

WebApplication app = builder.Build();

app.MapDefaultEndpoints();
app.MapProcessingEndpoints<AppliedTicketQueueItemRecord>();
app.MapControllers();
app.MapAuguryOpenApi();

app.Run();

public partial class Program;
