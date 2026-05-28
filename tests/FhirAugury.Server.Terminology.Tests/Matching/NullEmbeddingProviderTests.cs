using FhirAugury.Server.Terminology.Matching.Embeddings;

namespace FhirAugury.Server.Terminology.Tests.Matching;

public class NullEmbeddingProviderTests
{
    [Fact]
    public void IsEnabled_IsAlwaysFalse()
    {
        NullEmbeddingProvider provider = new();
        Assert.False(provider.IsEnabled);
        Assert.Equal(0, provider.Dimensions);
        Assert.Equal("none", provider.ModelName);
    }

    [Fact]
    public async Task EmbedAsync_Throws()
    {
        NullEmbeddingProvider provider = new();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.EmbedAsync("hi", default));
    }

    [Fact]
    public async Task EmbedBatchAsync_Throws()
    {
        NullEmbeddingProvider provider = new();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.EmbedBatchAsync(["hi", "there"], default));
    }
}
