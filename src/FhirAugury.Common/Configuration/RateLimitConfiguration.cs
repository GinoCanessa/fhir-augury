namespace FhirAugury.Common.Configuration;

/// <summary>
/// Shared rate limiting configuration for source services.
/// </summary>
public class RateLimitConfiguration
{
    public int MaxRequestsPerSecond { get; set; } = 10;
    public int BackoffBaseSeconds { get; set; } = 2;
    public int MaxRetries { get; set; } = 3;
}
