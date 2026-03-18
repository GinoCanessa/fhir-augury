namespace FhirAugury.Sources.Zulip;

/// <summary>Configuration options for the Zulip data source.</summary>
public record ZulipSourceOptions
{
    /// <summary>Base URL of the Zulip server.</summary>
    public string BaseUrl { get; init; } = "https://chat.fhir.org";

    /// <summary>Path to a .zuliprc file for authentication.</summary>
    public string? CredentialFile { get; init; }

    /// <summary>Zulip bot or user email for API authentication.</summary>
    public string? Email { get; init; }

    /// <summary>Zulip API key for authentication.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Number of messages to fetch per request.</summary>
    public int BatchSize { get; init; } = 1000;

    /// <summary>Only fetch web-public streams.</summary>
    public bool OnlyWebPublic { get; init; } = true;
}
