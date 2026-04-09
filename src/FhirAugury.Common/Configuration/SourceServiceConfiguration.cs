using System.ComponentModel.DataAnnotations;

namespace FhirAugury.Common.Configuration;

/// <summary>
/// Common configuration for source services.
/// </summary>
public class SourceServiceConfiguration
{
    /// <summary>HTTP port for REST API.</summary>
    [Range(1, 65535)]
    public int HttpPort { get; set; }

    /// <summary>Path to the SQLite database file.</summary>
    [Required]
    public string DatabasePath { get; set; } = "./data/source.db";

    /// <summary>Path to the file-system cache directory.</summary>
    [Required]
    public string CachePath { get; set; } = "./cache";

    /// <summary>Sync schedule as a TimeSpan string (e.g., "01:00:00").</summary>
    [Required]
    public string SyncSchedule { get; set; } = "01:00:00";

    /// <summary>
    /// Minimum age of the last sync before a new sync is triggered on startup.
    /// Prevents redundant downloads when services are restarted frequently.
    /// TimeSpan string (e.g., "04:00:00" = 4 hours). Default is 4 hours.
    /// </summary>
    public string MinSyncAge { get; set; } = "04:00:00";

    /// <summary>
    /// HTTP address of the orchestrator service (e.g., "http://localhost:5150").
    /// When set, the source will notify the orchestrator after ingestion completes.
    /// </summary>
    public string? OrchestratorAddress { get; set; }

    /// <summary>Rate limiting configuration.</summary>
    public RateLimitingConfiguration RateLimiting { get; set; } = new();
}

/// <summary>
/// Rate limiting configuration for API clients.
/// </summary>
public class RateLimitingConfiguration
{
    [Range(1, 1000)]
    public int MaxRequestsPerSecond { get; set; } = 10;

    [Range(1, 300)]
    public int BackoffBaseSeconds { get; set; } = 2;

    [Range(0, 10)]
    public int MaxRetries { get; set; } = 3;
}
