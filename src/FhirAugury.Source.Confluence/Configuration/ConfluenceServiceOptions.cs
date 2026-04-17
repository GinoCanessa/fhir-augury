using FhirAugury.Common.Configuration;

namespace FhirAugury.Source.Confluence.Configuration;

/// <summary>
/// Strongly-typed configuration for the Confluence source service.
/// </summary>
public class ConfluenceServiceOptions
{
    public const string SectionName = "Confluence";

    public string BaseUrl { get; set; } = "https://confluence.hl7.org";
    public string AuthMode { get; set; } = "cookie";
    public string? Cookie { get; set; }
    public string? Username { get; set; }
    public string? ApiToken { get; set; }
    public IReadOnlyList<string> Spaces { get; set; } = ["FHIR", "FHIRI", "SOA"];
    public string CachePath { get; set; } = "./cache";
    public string DatabasePath { get; set; } = "./data/confluence.db";
    public string SyncSchedule { get; set; } = "1.00:00:00";

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
    /// When true, the scheduled ingestion worker runs exactly one pass at
    /// startup (honoring <see cref="MinSyncAge"/> and <see cref="IngestionPaused"/>)
    /// and then exits its loop cleanly. The service itself keeps running, so HTTP
    /// endpoints and manual ingestion remain available. Useful for local/dev
    /// runs where a continuous sync loop is not desired.
    /// </summary>
    public bool RunIngestionOnStartupOnly { get; set; } = false;

    /// <summary>
    /// When true, rebuilds the database from cached responses on startup.
    /// </summary>
    public bool ReloadFromCacheOnStartup { get; set; } = false;

    public int PageSize { get; set; } = 25;
    public PortConfiguration Ports { get; set; } = new() { Http = 5180 };
    public RateLimitConfiguration RateLimiting { get; set; } = new();
    public AuxiliaryDatabaseOptions AuxiliaryDatabase { get; set; } = new();
    public DictionaryDatabaseOptions DictionaryDatabase { get; set; } = new();
    public Bm25Options Bm25 { get; set; } = new();
}
