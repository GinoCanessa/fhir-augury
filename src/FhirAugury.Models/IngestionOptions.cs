namespace FhirAugury.Models;

/// <summary>Configuration options for an ingestion run.</summary>
public record IngestionOptions
{
    /// <summary>Path to the SQLite database file.</summary>
    public required string DatabasePath { get; init; }

    /// <summary>Optional source-specific filter (e.g., JQL for Jira).</summary>
    public string? Filter { get; init; }

    /// <summary>Enable verbose output.</summary>
    public bool Verbose { get; init; }
}
