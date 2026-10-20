namespace FhirAugury.Server.Terminology.Configuration;

/// <summary>
/// Per-feature weights used by the lexical scoring pipeline (Phase 3)
/// and the hybrid match-mode combiner (Phase 5).
/// </summary>
/// <remarks>
/// Defined in Phase 2 so the binding surface is stable; matchers in
/// later phases consume these values directly.
/// </remarks>
public class LexicalWeightsOptions
{
    public double Url { get; set; } = 0.35;
    public double Title { get; set; } = 0.25;
    public double Name { get; set; } = 0.20;
    public double Description { get; set; } = 0.10;
    public double Concepts { get; set; } = 0.10;
}

/// <summary>
/// Combiner weights for <c>mode = "hybrid"</c>. Must sum to 1.0
/// (validated by <see cref="TerminologyServiceOptions.Validate"/>).
/// </summary>
public class HybridWeightsOptions
{
    public double Lexical { get; set; } = 0.6;
    public double Embeddings { get; set; } = 0.4;
}
