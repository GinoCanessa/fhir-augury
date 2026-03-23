namespace FhirAugury.Common.Tests;

public class CacheFileNamingTests
{
    [Fact]
    public void TryParse_DayOfPattern_Succeeds()
    {
        var result = Caching.CacheFileNaming.TryParse("DayOf_2025-01-15-001.xml", out var parsed);
        Assert.True(result);
        Assert.Equal(Caching.CacheFileNaming.BatchPrefix.DayOf, parsed!.Prefix);
        Assert.Equal(new DateOnly(2025, 1, 15), parsed.Date);
        Assert.Equal(1, parsed.SequenceNumber);
    }

    [Fact]
    public void TryParse_WeekOfPattern_Succeeds()
    {
        var result = Caching.CacheFileNaming.TryParse("_WeekOf_2025-01-13-000.json", out var parsed);
        Assert.True(result);
        Assert.Equal(Caching.CacheFileNaming.BatchPrefix.WeekOf, parsed!.Prefix);
        Assert.Equal(new DateOnly(2025, 1, 13), parsed.Date);
        Assert.Equal(0, parsed.SequenceNumber);
    }

    [Fact]
    public void SortForIngestion_OrdersByDateThenPrefixThenSequence()
    {
        var files = new[]
        {
            new Caching.CacheFileNaming.ParsedBatchFile("b", Caching.CacheFileNaming.BatchPrefix.DayOf, new DateOnly(2025, 1, 15), 1),
            new Caching.CacheFileNaming.ParsedBatchFile("a", Caching.CacheFileNaming.BatchPrefix.WeekOf, new DateOnly(2025, 1, 13), 0),
            new Caching.CacheFileNaming.ParsedBatchFile("c", Caching.CacheFileNaming.BatchPrefix.DayOf, new DateOnly(2025, 1, 13), 0),
        };

        var sorted = Caching.CacheFileNaming.SortForIngestion(files).ToList();
        Assert.Equal("a", sorted[0].FileName);
        Assert.Equal("c", sorted[1].FileName);
        Assert.Equal("b", sorted[2].FileName);
    }
}
