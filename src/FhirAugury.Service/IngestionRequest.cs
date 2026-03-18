using FhirAugury.Models;

namespace FhirAugury.Service;

/// <summary>Represents a request to ingest data from a source.</summary>
public record IngestionRequest
{
    public required string SourceName { get; init; }
    public required IngestionType Type { get; init; }
    public string? Identifier { get; init; }
    public string? Filter { get; init; }
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
}
