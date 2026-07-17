using FhirAugury.Common.Caching;

namespace FhirAugury.Common.Tests;

public class CacheFileNamingTests
{
    [Fact]
    public void TryParse_Roundtrips_RangeFilename()
    {
        bool ok = CacheFileNaming.TryParse("20200101-20201231-000.xml", out CacheFileNaming.ParsedCacheFile? parsed);
        Assert.True(ok);
        Assert.Equal(new DateOnly(2020, 1, 1), parsed!.StartDate);
        Assert.Equal(new DateOnly(2020, 12, 31), parsed.EndDate);
        Assert.Equal(0, parsed.SequenceNumber);
        Assert.Equal("xml", parsed.Extension);
    }

    [Fact]
    public void TryParse_Roundtrips_SingleDayFilename()
    {
        bool ok = CacheFileNaming.TryParse("20260417-20260417-000.xml", out CacheFileNaming.ParsedCacheFile? parsed);
        Assert.True(ok);
        Assert.Equal(new DateOnly(2026, 4, 17), parsed!.StartDate);
        Assert.Equal(new DateOnly(2026, 4, 17), parsed.EndDate);
        Assert.Equal(0, parsed.SequenceNumber);
    }

    [Fact]
    public void TryParse_RejectsInvalidOrdering()
    {
        bool ok = CacheFileNaming.TryParse("20261231-20200101-000.xml", out CacheFileNaming.ParsedCacheFile? parsed);
        Assert.False(ok);
        Assert.Null(parsed);
    }

    [Theory]
    [InlineData("DayOf_2020-01-01-000.xml")]
    [InlineData("_WeekOf_2020-01-06-000.json")]
    [InlineData("random.xml")]
    [InlineData("")]
    public void TryParse_RejectsLegacyPatterns(string fileName)
    {
        bool ok = CacheFileNaming.TryParse(fileName, out CacheFileNaming.ParsedCacheFile? parsed);
        Assert.False(ok);
        Assert.Null(parsed);
    }

    [Fact]
    public void GenerateFileName_IncrementsSequence_PerRange()
    {
        List<string> existing =
        [
            "20200101-20200107-000.xml",
            "20200101-20200107-001.xml",
        ];
        string next = CacheFileNaming.GenerateFileName(new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 7), "xml", existing);
        Assert.Equal("20200101-20200107-002.xml", next);
    }

    [Fact]
    public void GenerateFileName_IncrementsSequence_OnlyForMatchingRange()
    {
        List<string> existing =
        [
            "20200101-20200107-005.xml",
            "20200101-20200101-000.xml",
        ];
        string next = CacheFileNaming.GenerateFileName(new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 1), "xml", existing);
        Assert.Equal("20200101-20200101-001.xml", next);
    }

    [Fact]
    public void GenerateFileName_SingleDayOverload_SetsEndEqualStart()
    {
        string name = CacheFileNaming.GenerateFileName(new DateOnly(2026, 4, 17), "xml", []);
        Assert.Equal("20260417-20260417-000.xml", name);
    }

    [Fact]
    public void GenerateFileName_Throws_WhenStartAfterEnd()
    {
        Assert.Throws<ArgumentException>(() =>
            CacheFileNaming.GenerateFileName(new DateOnly(2020, 6, 1), new DateOnly(2020, 5, 1), "xml", []));
    }

    [Fact]
    public void SortForIngestion_WidestRangeFirst_OnSameStart()
    {
        CacheFileNaming.ParsedCacheFile[] files =
        [
            new("a", new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 1), 0, "xml"),
            new("b", new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 7), 0, "xml"),
            new("c", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), 0, "xml"),
        ];
        List<CacheFileNaming.ParsedCacheFile> sorted = CacheFileNaming.SortForIngestion(files).ToList();
        Assert.Equal("c", sorted[0].FileName);
        Assert.Equal("b", sorted[1].FileName);
        Assert.Equal("a", sorted[2].FileName);
    }

    [Fact]
    public void SortForIngestion_StableSequenceTieBreak()
    {
        CacheFileNaming.ParsedCacheFile[] files =
        [
            new("seq2", new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 1), 2, "xml"),
            new("seq0", new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 1), 0, "xml"),
            new("seq1", new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 1), 1, "xml"),
        ];
        List<CacheFileNaming.ParsedCacheFile> sorted = CacheFileNaming.SortForIngestion(files).ToList();
        Assert.Equal("seq0", sorted[0].FileName);
        Assert.Equal("seq1", sorted[1].FileName);
        Assert.Equal("seq2", sorted[2].FileName);
    }

    [Fact]
    public void SortForIngestion_RangesBeforeOverlappingSingleDays()
    {
        CacheFileNaming.ParsedCacheFile[] files =
        [
            new("mar15", new DateOnly(2020, 3, 15), new DateOnly(2020, 3, 15), 0, "xml"),
            new("year", new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), 0, "xml"),
            new("today", new DateOnly(2026, 4, 17), new DateOnly(2026, 4, 17), 0, "xml"),
        ];
        List<CacheFileNaming.ParsedCacheFile> sorted = CacheFileNaming.SortForIngestion(files).ToList();
        Assert.Equal("year", sorted[0].FileName);
        Assert.Equal("mar15", sorted[1].FileName);
        Assert.Equal("today", sorted[2].FileName);
    }
}
