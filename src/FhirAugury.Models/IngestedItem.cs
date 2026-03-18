namespace FhirAugury.Models;

/// <summary>Represents a single item processed during ingestion.</summary>
public record IngestedItem
{
    /// <summary>The source type (e.g., "jira", "zulip").</summary>
    public required string SourceType { get; init; }

    /// <summary>The source-specific identifier (e.g., issue key).</summary>
    public required string SourceId { get; init; }

    /// <summary>The item title or subject line.</summary>
    public required string Title { get; init; }

    /// <summary>Text fields available for cross-reference scanning.</summary>
    public required IReadOnlyList<string> SearchableTextFields { get; init; }
}
