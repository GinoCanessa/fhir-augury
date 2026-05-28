namespace FhirAugury.Server.Terminology.Configuration;

/// <summary>
/// Default values applied when a <c>/check</c> request omits the
/// corresponding field. Surfaces here so operators can tune
/// throughput/quality tradeoffs without restarting clients.
/// </summary>
public class DefaultsOptions
{
    /// <summary>Default <c>Limit</c> when the caller omits it.</summary>
    public int Limit { get; set; } = 10;

    /// <summary>Default minimum score threshold for returned matches.</summary>
    public double MinScore { get; set; } = 0.1;

    /// <summary>Default match mode: <c>lexical</c>, <c>embeddings</c>, or <c>hybrid</c>.</summary>
    public string Mode { get; set; } = "lexical";
}
