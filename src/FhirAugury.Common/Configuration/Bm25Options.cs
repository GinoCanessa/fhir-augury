namespace FhirAugury.Common.Configuration;

/// <summary>
/// BM25 scoring parameters for keyword indexing.
/// </summary>
public class Bm25Options
{
    /// <summary>
    /// Term frequency saturation parameter. Higher values increase the impact of
    /// additional term occurrences. Typical range: 1.2–2.0.
    /// </summary>
    public double K1 { get; set; } = 1.2;

    /// <summary>
    /// Document length normalization parameter. 0 = no normalization,
    /// 1 = full normalization. Typical value: 0.75.
    /// </summary>
    public double B { get; set; } = 0.75;
}
