using FhirAugury.Common.Configuration;

namespace FhirAugury.Source.Jira.Configuration;

/// <summary>
/// Strongly-typed configuration for the Jira source service.
/// </summary>
public class JiraServiceOptions
{
    public const string SectionName = "Jira";

    public string BaseUrl { get; set; } = "https://jira.hl7.org";
    public string AuthMode { get; set; } = "cookie";
    public string? Cookie { get; set; }
    public string? ApiToken { get; set; }
    public string? Email { get; set; }
    public string CachePath { get; set; } = "./cache";
    public string DatabasePath { get; set; } = "./data/jira.db";
    public string SyncSchedule { get; set; } = "01:00:00";

    /// <summary>
    /// Minimum age of the last sync before a new sync is triggered on startup.
    /// Prevents redundant downloads when services are restarted frequently.
    /// </summary>
    public string MinSyncAge { get; set; } = "04:00:00";

    /// <summary>HTTP address of the orchestrator service for ingestion notifications.</summary>
    public string? OrchestratorAddress { get; set; }

    /// <summary>
    /// When true, pauses all ingestion (scheduled and on-demand). The service remains
    /// available for queries but will not download new content.
    /// </summary>
    public bool IngestionPaused { get; set; } = false;

    /// <summary>
    /// When true, rebuilds the database from cached responses on startup.
    /// </summary>
    public bool ReloadFromCacheOnStartup { get; set; } = false;

    public string DefaultProject { get; set; } = "FHIR";
    public string? DefaultJql { get; set; }
    public int PageSize { get; set; } = 100;
    public PortConfiguration Ports { get; set; } = new() { Http = 5160 };
    public RateLimitConfiguration RateLimiting { get; set; } = new();
    public AuxiliaryDatabaseOptions AuxiliaryDatabase { get; set; } = new();
    public DictionaryDatabaseOptions DictionaryDatabase { get; set; } = new();
    public Bm25Options Bm25 { get; set; } = new();
}
