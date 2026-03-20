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
    public string CachePath { get; set; } = "./cache/zulip";
    public string DatabasePath { get; set; } = "./data/zulip.db";
    public string SyncSchedule { get; set; } = "04:00:00";
    public bool OnlyWebPublic { get; set; } = true;
    public int BatchSize { get; set; } = 1000;
    public PortConfiguration Ports { get; set; } = new();
    public RateLimitConfiguration RateLimiting { get; set; } = new();
}

public class PortConfiguration
{
    public int Http { get; set; } = 5170;
    public int Grpc { get; set; } = 5171;
}

public class RateLimitConfiguration
{
    public int MaxRequestsPerSecond { get; set; } = 5;
    public int BackoffBaseSeconds { get; set; } = 2;
    public int MaxRetries { get; set; } = 3;
}
