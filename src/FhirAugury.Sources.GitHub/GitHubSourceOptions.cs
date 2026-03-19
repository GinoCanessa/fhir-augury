namespace FhirAugury.Sources.GitHub;

/// <summary>Configuration options for the GitHub data source.</summary>
public record GitHubSourceOptions
{
    /// <summary>GitHub personal access token for API authentication.</summary>
    public string? PersonalAccessToken { get; init; }

    /// <summary>GitHub repositories to ingest (owner/repo format).</summary>
    public IReadOnlyList<string> Repositories { get; init; } = ["HL7/fhir", "HL7/fhir-ig-publisher"];

    /// <summary>Number of results per page for API requests.</summary>
    public int PageSize { get; init; } = 100;

    /// <summary>Pause requests when remaining rate limit drops below this value.</summary>
    public int RateLimitBuffer { get; init; } = 100;
}
