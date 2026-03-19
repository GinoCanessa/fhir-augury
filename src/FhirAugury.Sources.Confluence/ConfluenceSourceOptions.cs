using FhirAugury.Models.Caching;

namespace FhirAugury.Sources.Confluence;

public enum ConfluenceAuthMode { Basic, Cookie }

/// <summary>Configuration options for the Confluence data source.</summary>
public record ConfluenceSourceOptions
{
    /// <summary>Base URL of the Confluence server.</summary>
    public string BaseUrl { get; init; } = "https://confluence.hl7.org";

    /// <summary>Authentication mode for the Confluence API.</summary>
    public ConfluenceAuthMode AuthMode { get; init; } = ConfluenceAuthMode.Cookie;

    /// <summary>Username for Basic authentication.</summary>
    public string? Username { get; init; }

    /// <summary>API token for Basic authentication.</summary>
    public string? ApiToken { get; init; }

    /// <summary>Session cookie for Cookie-based authentication.</summary>
    public string? Cookie { get; init; }

    /// <summary>Confluence spaces to ingest.</summary>
    public List<string> Spaces { get; init; } = ["FHIR", "FHIRI"];

    /// <summary>Number of results per page for API requests.</summary>
    public int PageSize { get; init; } = 25;

    /// <summary>Cache mode for this source.</summary>
    public CacheMode CacheMode { get; init; } = CacheMode.Disabled;

    /// <summary>Cache instance for this source.</summary>
    public IResponseCache? Cache { get; init; }
}
