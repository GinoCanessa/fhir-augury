using FhirAugury.Models.Caching;

namespace FhirAugury.Sources.Tests.Caching;

public class NullResponseCacheTests
{
    [Fact]
    public void TryGet_AlwaysReturnsFalse()
    {
        var result = NullResponseCache.Instance.TryGet("jira", "anything.json", out var stream);
        Assert.False(result);
        Assert.Same(Stream.Null, stream);
    }

    [Fact]
    public async Task PutAsync_DoesNotThrow()
    {
        using var ms = new MemoryStream("test"u8.ToArray());
        await NullResponseCache.Instance.PutAsync("jira", "test.json", ms, CancellationToken.None);
    }

    [Fact]
    public void EnumerateKeys_ReturnsEmpty()
    {
        Assert.Empty(NullResponseCache.Instance.EnumerateKeys("jira"));
        Assert.Empty(NullResponseCache.Instance.EnumerateKeys("zulip", "s270"));
    }

    [Fact]
    public void GetStats_ReturnsZeros()
    {
        var stats = NullResponseCache.Instance.GetStats("jira");
        Assert.Equal("jira", stats.Source);
        Assert.Equal(0, stats.FileCount);
        Assert.Equal(0, stats.TotalBytes);
        Assert.Empty(stats.SubPaths);
    }

    [Fact]
    public void RootPath_IsEmpty()
    {
        Assert.Equal(string.Empty, NullResponseCache.Instance.RootPath);
    }

    [Fact]
    public void Remove_DoesNotThrow()
    {
        NullResponseCache.Instance.Remove("jira", "anything");
    }

    [Fact]
    public void Clear_DoesNotThrow()
    {
        NullResponseCache.Instance.Clear("jira");
    }

    [Fact]
    public void ClearAll_DoesNotThrow()
    {
        NullResponseCache.Instance.ClearAll();
    }
}
