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
    public string CachePath { get; set; } = "./cache/confluence";
    public string DatabasePath { get; set; } = "./data/confluence.db";
    public string SyncSchedule { get; set; } = "1.00:00:00";
    public int PageSize { get; set; } = 25;
    public PortConfiguration Ports { get; set; } = new() { Http = 5180, Grpc = 5181 };
    public RateLimitConfiguration RateLimiting { get; set; } = new();
}
