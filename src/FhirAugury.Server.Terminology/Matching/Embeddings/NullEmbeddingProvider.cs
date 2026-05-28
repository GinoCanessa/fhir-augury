namespace FhirAugury.Server.Terminology.Matching.Embeddings;

/// <summary>
/// Sentinel provider that signals "no embedding backend is wired up".
/// Always reports <see cref="IsEnabled"/> as <c>false</c>; any actual
/// embed call throws.
/// </summary>
public sealed class NullEmbeddingProvider : IEmbeddingProvider
{
    public bool IsEnabled => false;
    public int Dimensions => 0;
    public string ModelName => "none";

    public Task<float[]> EmbedAsync(string text, CancellationToken ct) =>
        throw new InvalidOperationException("Embeddings are not enabled in this deployment.");

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
        throw new InvalidOperationException("Embeddings are not enabled in this deployment.");
}
