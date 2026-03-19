using FhirAugury.Models.Caching;

namespace FhirAugury.Sources.Tests.Caching;

public class CacheFileNamingTests
{
    [Theory]
    [InlineData("_WeekOf_2024-08-05.xml", CacheFileNaming.BatchPrefix.WeekOf, "2024-08-05", null)]
    [InlineData("DayOf_2025-11-05.xml", CacheFileNaming.BatchPrefix.DayOf, "2025-11-05", null)]
    [InlineData("DayOf_2026-03-18-000.xml", CacheFileNaming.BatchPrefix.DayOf, "2026-03-18", 0)]
    [InlineData("DayOf_2026-03-18-042.json", CacheFileNaming.BatchPrefix.DayOf, "2026-03-18", 42)]
    [InlineData("_WeekOf_2024-08-05-003.json", CacheFileNaming.BatchPrefix.WeekOf, "2024-08-05", 3)]
    public void TryParse_ValidPatterns_ParsesCorrectly(string fileName, CacheFileNaming.BatchPrefix expectedPrefix, string expectedDate, int? expectedSeq)
    {
        var result = CacheFileNaming.TryParse(fileName, out var parsed);

        Assert.True(result);
        Assert.Equal(expectedPrefix, parsed.Prefix);
        Assert.Equal(DateOnly.Parse(expectedDate), parsed.Date);
        Assert.Equal(expectedSeq, parsed.SequenceNumber);
        Assert.Equal(fileName, parsed.FileName);
    }

    [Theory]
    [InlineData("random-file.xml")]
    [InlineData("")]
    [InlineData("DayOf_2026-13-45.xml")]
    [InlineData("DayOf_2026-03-18-000")]
    [InlineData("_meta_jira.json")]
    [InlineData("download_manifest.txt")]
    public void TryParse_InvalidPatterns_ReturnsFalse(string fileName)
    {
        Assert.False(CacheFileNaming.TryParse(fileName, out _));
    }

    [Fact]
    public void SortForIngestion_DateAscending()
    {
        var files = new[]
        {
            new CacheFileNaming.ParsedBatchFile("DayOf_2026-03-20-000.xml", CacheFileNaming.BatchPrefix.DayOf, new DateOnly(2026, 3, 20), 0),
            new CacheFileNaming.ParsedBatchFile("DayOf_2026-03-18-000.xml", CacheFileNaming.BatchPrefix.DayOf, new DateOnly(2026, 3, 18), 0),
        };

        var sorted = CacheFileNaming.SortForIngestion(files).ToList();

        Assert.Equal("DayOf_2026-03-18-000.xml", sorted[0].FileName);
        Assert.Equal("DayOf_2026-03-20-000.xml", sorted[1].FileName);
    }

    [Fact]
    public void SortForIngestion_WeeklyBeforeDaily_SameDate()
    {
        var files = new[]
        {
            new CacheFileNaming.ParsedBatchFile("DayOf_2024-08-05.xml", CacheFileNaming.BatchPrefix.DayOf, new DateOnly(2024, 8, 5), null),
            new CacheFileNaming.ParsedBatchFile("_WeekOf_2024-08-05.xml", CacheFileNaming.BatchPrefix.WeekOf, new DateOnly(2024, 8, 5), null),
        };

        var sorted = CacheFileNaming.SortForIngestion(files).ToList();

        Assert.Equal("_WeekOf_2024-08-05.xml", sorted[0].FileName);
        Assert.Equal("DayOf_2024-08-05.xml", sorted[1].FileName);
    }

    [Fact]
    public void SortForIngestion_LegacyBeforeSequenced_SameDate()
    {
        var files = new[]
        {
            new CacheFileNaming.ParsedBatchFile("DayOf_2026-03-18-000.xml", CacheFileNaming.BatchPrefix.DayOf, new DateOnly(2026, 3, 18), 0),
            new CacheFileNaming.ParsedBatchFile("DayOf_2026-03-18.xml", CacheFileNaming.BatchPrefix.DayOf, new DateOnly(2026, 3, 18), null),
        };

        var sorted = CacheFileNaming.SortForIngestion(files).ToList();

        Assert.Equal("DayOf_2026-03-18.xml", sorted[0].FileName);
        Assert.Equal("DayOf_2026-03-18-000.xml", sorted[1].FileName);
    }

    [Fact]
    public void SortForIngestion_SequenceAscending()
    {
        var files = new[]
        {
            new CacheFileNaming.ParsedBatchFile("DayOf_2026-03-18-002.xml", CacheFileNaming.BatchPrefix.DayOf, new DateOnly(2026, 3, 18), 2),
            new CacheFileNaming.ParsedBatchFile("DayOf_2026-03-18-000.xml", CacheFileNaming.BatchPrefix.DayOf, new DateOnly(2026, 3, 18), 0),
            new CacheFileNaming.ParsedBatchFile("DayOf_2026-03-18-001.xml", CacheFileNaming.BatchPrefix.DayOf, new DateOnly(2026, 3, 18), 1),
        };

        var sorted = CacheFileNaming.SortForIngestion(files).ToList();

        Assert.Equal(0, sorted[0].SequenceNumber);
        Assert.Equal(1, sorted[1].SequenceNumber);
        Assert.Equal(2, sorted[2].SequenceNumber);
    }

    [Fact]
    public void SortForIngestion_EmptyList_ReturnsEmpty()
    {
        var sorted = CacheFileNaming.SortForIngestion([]).ToList();
        Assert.Empty(sorted);
    }

    [Fact]
    public void GenerateDaily_NoExisting()
    {
        var result = CacheFileNaming.GenerateDailyFileName(new DateOnly(2026, 3, 18), "xml", []);
        Assert.Equal("DayOf_2026-03-18-000.xml", result);
    }

    [Fact]
    public void GenerateDaily_WithExisting()
    {
        var existing = new[] { "DayOf_2026-03-18-000.xml", "DayOf_2026-03-18-001.xml" };
        var result = CacheFileNaming.GenerateDailyFileName(new DateOnly(2026, 3, 18), "xml", existing);
        Assert.Equal("DayOf_2026-03-18-002.xml", result);
    }

    [Fact]
    public void GenerateWeekly_NormalizesToMonday()
    {
        // 2024-08-07 is a Wednesday; Monday is 2024-08-05
        var result = CacheFileNaming.GenerateWeeklyFileName(new DateOnly(2024, 8, 7), "json", []);
        Assert.Equal("_WeekOf_2024-08-05-000.json", result);
    }

    [Fact]
    public void GenerateWeekly_WithExisting()
    {
        var existing = new[] { "_WeekOf_2024-08-05-000.json" };
        var result = CacheFileNaming.GenerateWeeklyFileName(new DateOnly(2024, 8, 5), "json", existing);
        Assert.Equal("_WeekOf_2024-08-05-001.json", result);
    }
}
