namespace FhirAugury.Server.Terminology.Configuration;

/// <summary>
/// Embeddings sub-configuration. Phase 2 ships this surface with
/// <see cref="Enabled"/> = <c>false</c> and <see cref="Provider"/> =
/// <c>"none"</c>; Phase 5 wires <c>NullEmbeddingProvider</c>
/// behind it. Any non-<c>"none"</c> provider is rejected by
/// <see cref="TerminologyServiceOptions.Validate"/> until a real
/// provider ships.
/// </summary>
public class EmbeddingsOptions
{
    public bool Enabled { get; set; } = false;

    /// <summary>Provider name. Only <c>"none"</c> is accepted in v1.</summary>
    public string Provider { get; set; } = "none";

    public string? ProviderEndpoint { get; set; }

    public int? Dimensions { get; set; }

    public string? Model { get; set; }
}
