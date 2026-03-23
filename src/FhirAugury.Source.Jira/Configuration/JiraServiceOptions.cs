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
    public string CachePath { get; set; } = "./cache/jira";
    public string DatabasePath { get; set; } = "./data/jira.db";
    public string SyncSchedule { get; set; } = "01:00:00";
    public string DefaultProject { get; set; } = "FHIR";
    public string? DefaultJql { get; set; }
    public int PageSize { get; set; } = 100;
    public PortConfiguration Ports { get; set; } = new() { Http = 5160, Grpc = 5161 };
    public RateLimitConfiguration RateLimiting { get; set; } = new();
}
