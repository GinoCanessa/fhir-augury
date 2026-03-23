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
    public string CachePath { get; set; } = "./cache/zulip";
    public string DatabasePath { get; set; } = "./data/zulip.db";
    public string SyncSchedule { get; set; } = "04:00:00";
    public bool OnlyWebPublic { get; set; } = true;
    public int BatchSize { get; set; } = 1000;
    public PortConfiguration Ports { get; set; } = new() { Http = 5170, Grpc = 5171 };
    public RateLimitConfiguration RateLimiting { get; set; } = new();
}
