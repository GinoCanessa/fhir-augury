using System.Linq;
using FhirAugury.Common.OpenApi;
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
    return database;
});

// The attributor resolves orchestrator-first / Jira-source fallback per call
// from BallotNotesHydrationOptions, so the typed client needs no base address.
builder.Services.AddHttpClient<TicketAttributor>();
builder.Services.AddSingleton<BallotNotesHydrator>();

WebApplication app = builder.Build();

app.MapDefaultEndpoints();
app.MapControllers();
app.MapAuguryOpenApi();

app.Run();

public partial class Program;
