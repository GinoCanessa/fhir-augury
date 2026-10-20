namespace FhirAugury.Server.Terminology.Matching.Embeddings;

/// <summary>
/// Abstracts the embedding model the <c>EmbeddingMatcher</c> and
/// <c>HybridMatcher</c> consume. v1 ships only
/// <see cref="NullEmbeddingProvider"/>; a follow-up plan can drop in
/// an OpenAI-compatible HTTP provider without touching the matchers.
/// </summary>
public interface IEmbeddingProvider
{
    bool IsEnabled { get; }
    int Dimensions { get; }
    string ModelName { get; }
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
