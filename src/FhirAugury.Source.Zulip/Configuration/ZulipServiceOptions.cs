using FhirAugury.Common.Configuration;

namespace FhirAugury.Source.Zulip.Configuration;

/// <summary>
/// Strongly-typed configuration for the Zulip source service.
/// </summary>
public class ZulipServiceOptions
{
    public const string SectionName = "Zulip";

    public string BaseUrl { get; set; } = "https://chat.fhir.org";
    public string? CredentialFile { get; set; }
    public string? Email { get; set; }
    public string? ApiKey { get; set; }
    public string CachePath { get; set; } = "./cache";
    public string DatabasePath { get; set; } = "./data/zulip.db";
    public string SyncSchedule { get; set; } = "04:00:00";

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

    public bool ReloadFromCacheOnStartup { get; set; } = false;

    /// <summary>
    /// Force a full rebuild of Jira ticket reference indexes on startup.
    /// This is independent of <see cref="ReloadFromCacheOnStartup"/>, which
    /// already triggers ticket re-indexing as part of a full cache rebuild.
    /// A new database creation also triggers ticket indexing automatically.
    /// </summary>
    public bool ReindexTicketsOnStartup { get; set; } = false;

    public List<int> ExcludedStreamIds { get; set; } = [];

    /// <summary>
    /// Per-stream baseline ranking values (0–10). Streams not listed default to 5.
    /// Keys are stream names (case-insensitive match). Lower values reduce search
    /// ranking for noisy/low-value streams (e.g. build notifications).
    /// </summary>
    public Dictionary<string, int> StreamBaselineValues { get; set; } = [];

    public bool OnlyWebPublic { get; set; } = true;
    public int BatchSize { get; set; } = 1000;
    public PortConfiguration Ports { get; set; } = new() { Http = 5170 };
    public RateLimitConfiguration RateLimiting { get; set; } = new();
    public AuxiliaryDatabaseOptions AuxiliaryDatabase { get; set; } = new();
    public DictionaryDatabaseOptions DictionaryDatabase { get; set; } = new();
    public Bm25Options Bm25 { get; set; } = new();
}
