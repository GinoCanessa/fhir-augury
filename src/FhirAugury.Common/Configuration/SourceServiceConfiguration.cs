namespace FhirAugury.Common.Configuration;

/// <summary>
/// Common configuration for source services.
/// </summary>
public class SourceServiceConfiguration
{
    /// <summary>HTTP port for REST API.</summary>
    public int HttpPort { get; set; }

    /// <summary>gRPC port for inter-service communication.</summary>
    public int GrpcPort { get; set; }

    /// <summary>Path to the SQLite database file.</summary>
    public string DatabasePath { get; set; } = "./data/source.db";

    /// <summary>Path to the file-system cache directory.</summary>
    public string CachePath { get; set; } = "./cache";

    /// <summary>Sync schedule as a TimeSpan string (e.g., "01:00:00").</summary>
    public string SyncSchedule { get; set; } = "01:00:00";

    /// <summary>Rate limiting configuration.</summary>
    public RateLimitingConfiguration RateLimiting { get; set; } = new();
}

/// <summary>
/// Rate limiting configuration for API clients.
/// </summary>
public class RateLimitingConfiguration
{
    public int MaxRequestsPerSecond { get; set; } = 10;
    public int BackoffBaseSeconds { get; set; } = 2;
    public int MaxRetries { get; set; } = 3;
}
