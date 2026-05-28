namespace FhirAugury.Server.Terminology.Models;

/// <summary>
/// Per-request parameters for an overlap-check call. All members are
/// optional from the wire perspective; nulls map to the service-wide
/// defaults configured under <c>Terminology:Defaults</c>.
/// </summary>
public sealed record OverlapCheckRequest
{
    /// <summary>"lexical", "embeddings", or "hybrid".</summary>
    public string? Mode { get; init; }

    /// <summary>Maximum number of ranked candidates to return.</summary>
    public int? Limit { get; init; }

    /// <summary>Inclusive minimum score; candidates below it are dropped.</summary>
    public double? MinScore { get; init; }
}
