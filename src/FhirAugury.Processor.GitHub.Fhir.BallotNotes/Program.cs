using System.Linq;
using FhirAugury.Common.OpenApi;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Authoring;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Attribution;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("FHIR_AUGURY_BALLOTNOTES_");

builder.AddServiceDefaults();

IConfigurationSection portsSection = builder.Configuration.GetSection($"{BallotNotesServiceOptions.SectionName}:Ports");
int httpPort = portsSection.GetValue("Http", 5174);
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(httpPort, o => o.Protocols = HttpProtocols.Http1AndHttp2);
});

builder.Services.AddControllers();
builder.Services.AddAuguryOpenApi(o =>
{
    o.Title = "FHIR Augury Processor: GitHub FHIR BallotNotes";
    o.Description = "Ballot-note hydration and authoring API backing the notes-site renderer.";
});

builder.Services.AddOptions<BallotNotesServiceOptions>()
    .Bind(builder.Configuration.GetSection(BallotNotesServiceOptions.SectionName))
    .Validate(options => !options.Validate().Any(), "BallotNotes configuration is invalid.")
    .ValidateOnStart();
builder.Services.AddSingleton<IOptions<ProcessingServiceOptions>>(sp =>
    Options.Create<ProcessingServiceOptions>(
        sp.GetRequiredService<IOptions<BallotNotesServiceOptions>>().Value));

builder.Services.AddOptions<BallotNotesHydrationOptions>()
    .Bind(builder.Configuration.GetSection($"{BallotNotesServiceOptions.SectionName}:Hydration"))
    .Validate(options => !options.Validate().Any(), "BallotNotes:Hydration configuration is invalid.")
    .ValidateOnStart();

builder.Services.AddSingleton(sp =>
{
    BallotNotesServiceOptions options = sp.GetRequiredService<IOptions<BallotNotesServiceOptions>>().Value;
    string dbPath = Path.GetFullPath(options.DatabasePath);
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    BallotNotesDatabase database = new(dbPath, sp.GetRequiredService<ILogger<BallotNotesDatabase>>());
    database.Initialize();
    database.AcquireStartupOwnership();
    database.RecoverInterruptedHydrationAsync()
        .GetAwaiter()
        .GetResult();
    new AuthoringRunStore(database.OpenConnection)
        .EnsureProcessorModeAsync(BallotNotesDatabase.AuthoringProcessorKind)
        .GetAwaiter()
        .GetResult();
    return database;
});

// The attributor resolves orchestrator-first / Jira-source fallback per call
// from BallotNotesHydrationOptions, so the typed client needs no base address.
// Bound the connect phase so best-effort attribution fails fast against an
// unreachable/black-holed upstream instead of stalling on the OS connect timeout.
builder.Services.AddHttpClient<TicketAttributor>()
    .ConfigurePrimaryHttpMessageHandler(static sp =>
    {
        BallotNotesHydrationOptions hydration = sp
            .GetRequiredService<IOptions<BallotNotesHydrationOptions>>().Value;
        return new SocketsHttpHandler
        {
            ConnectTimeout = hydration.AttributionConnectTimeout,
        };
    });
builder.Services.AddSingleton<BallotNotesHydrator>();
builder.Services.AddSingleton<AuthoringRunStore>(sp => new AuthoringRunStore(
    sp.GetRequiredService<BallotNotesDatabase>().OpenConnection,
    sp.GetRequiredService<ILogger<AuthoringRunStore>>()));
builder.Services.AddSingleton<ProcessingLifecycleService>();
builder.Services.AddSingleton<BallotNotesAuthoringRunCoordinator>();
builder.Services.AddSingleton<BallotNotesAuthoringWorkItemStore>();
builder.Services.AddSingleton<IAuthoringQueueStore<BallotNotesAuthoringWorkItem>>(sp =>
    sp.GetRequiredService<BallotNotesAuthoringWorkItemStore>());
builder.Services.AddSingleton<IBallotNotesAuthoringCommandRunner, BallotNotesAuthoringCommandRunner>();
builder.Services.AddSingleton<BallotNotesAuthoringHandler>();
builder.Services.AddSingleton<IAuthoringWorkItemHandler<BallotNotesAuthoringWorkItem>>(sp =>
    sp.GetRequiredService<BallotNotesAuthoringHandler>());
builder.Services.AddSingleton<AuthoringQueueRunner<BallotNotesAuthoringWorkItem>>();
builder.Services.AddHostedService<BallotNotesAuthoringHostedService>();
builder.Services.AddSingleton<SqliteReviewSnapshotReconciler>();
builder.Services.AddSingleton<BallotNotesRunPostProcessor>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<BallotNotesRunPostProcessor>());

WebApplication app = builder.Build();

// Eagerly initialize the database so its schema exists for the notes-site
// renderer even before the first hydrate request (the singleton is otherwise
// created lazily on first controller resolution).
_ = app.Services.GetRequiredService<BallotNotesDatabase>();

app.MapDefaultEndpoints();
app.MapControllers();
app.MapAuguryOpenApi();

app.Run();

public partial class Program;

internal sealed class BallotNotesAuthoringHostedService(
    AuthoringQueueRunner<BallotNotesAuthoringWorkItem> runner,
    AuthoringRunStore store,
    BallotNotesAuthoringRunCoordinator coordinator)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            string mode = (await store.EnsureProcessorModeAsync(
                coordinator.ProcessorKind,
                ct: stoppingToken)).Mode;
            if (string.Equals(
                mode,
                AuthoringStatusValues.ProcessorModes.RunBacked,
                StringComparison.Ordinal))
            {
                await runner.RunAsync(stoppingToken);
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
