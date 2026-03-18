using FhirAugury.Database;
using FhirAugury.Service;
using FhirAugury.Service.Api;
using FhirAugury.Service.Workers;

var builder = WebApplication.CreateBuilder(args);

// Configuration: appsettings.json, appsettings.local.json, env vars, user secrets
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("FHIR_AUGURY_");

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<AuguryConfiguration>(optional: true);
}

// Bind configuration
builder.Services.Configure<AuguryConfiguration>(
    builder.Configuration.GetSection(AuguryConfiguration.SectionName));

var auguryConfig = builder.Configuration
    .GetSection(AuguryConfiguration.SectionName)
    .Get<AuguryConfiguration>() ?? new AuguryConfiguration();

// Apply configured listen port
builder.WebHost.UseUrls($"http://*:{auguryConfig.Api.Port}");

// Database — registered via factory so tests can override
builder.Services.AddSingleton(sp =>
{
    var db = new DatabaseService(auguryConfig.DatabasePath);
    db.InitializeDatabase();
    return db;
});

// Ingestion queue
builder.Services.AddSingleton<IngestionQueue>();

// Background workers
builder.Services.AddSingleton<IngestionWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IngestionWorker>());
builder.Services.AddSingleton<ScheduledIngestionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ScheduledIngestionService>());

// HTTP client factory for sources
builder.Services.AddHttpClient();

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var origins = auguryConfig.Api.CorsOrigins;
        if (origins.Length == 0 || origins.Contains("*"))
        {
            policy.AllowAnyOrigin();
        }
        else
        {
            policy.WithOrigins(origins);
        }

        policy.AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors();
app.MapAuguryApi();

// Health check
app.MapGet("/health", () => Results.Ok(new { Status = "healthy", Timestamp = DateTimeOffset.UtcNow }));

app.Run();

// Partial class for WebApplicationFactory test access
public partial class Program;
