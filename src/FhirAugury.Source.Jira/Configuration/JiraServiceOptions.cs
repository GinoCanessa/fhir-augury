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
    public string CachePath { get; set; } = "./cache/jira";
    public string DatabasePath { get; set; } = "./data/jira.db";
    public string SyncSchedule { get; set; } = "01:00:00";
    public string DefaultProject { get; set; } = "FHIR";
    public string? DefaultJql { get; set; }
    public int PageSize { get; set; } = 100;
    public PortConfiguration Ports { get; set; } = new();
    public RateLimitConfiguration RateLimiting { get; set; } = new();
}

public class PortConfiguration
{
    public int Http { get; set; } = 5160;
    public int Grpc { get; set; } = 5161;
}

public class RateLimitConfiguration
{
    public int MaxRequestsPerSecond { get; set; } = 10;
    public int BackoffBaseSeconds { get; set; } = 2;
    public int MaxRetries { get; set; } = 3;
}
