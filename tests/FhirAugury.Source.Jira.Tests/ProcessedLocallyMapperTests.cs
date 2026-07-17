using System.Globalization;
using System.Text;
using FhirAugury.Source.Jira.Indexing;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Jira.Tests;

public class ProcessedLocallyMapperTests
{
    [Fact]
    public void ToStorageValue_True_ReturnsCurrentUtcIsoString()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-5);
        object result = ProcessedLocallyMapper.ToStorageValue(true);
        DateTimeOffset after = DateTimeOffset.UtcNow.AddSeconds(5);

        string? asString = Assert.IsType<string>(result);
        DateTimeOffset parsed = DateTimeOffset.Parse(asString, CultureInfo.InvariantCulture);
        Assert.InRange(parsed, before, after);
    }

    [Fact]
    public void ToStorageValue_False_ReturnsDbNull()
    {
        Assert.Equal(DBNull.Value, ProcessedLocallyMapper.ToStorageValue(false));
    }

    [Fact]
    public void ToStorageValue_Null_ReturnsDbNull()
    {
        Assert.Equal(DBNull.Value, ProcessedLocallyMapper.ToStorageValue(null));
    }

    [Fact]
    public void FromStorageValue_Null_ReturnsFalse()
    {
        Assert.False(ProcessedLocallyMapper.FromStorageValue(null));
    }

    [Fact]
    public void FromStorageValue_AnyNonNull_ReturnsTrue()
    {
        Assert.True(ProcessedLocallyMapper.FromStorageValue(DateTimeOffset.UtcNow));
        Assert.True(ProcessedLocallyMapper.FromStorageValue(DateTimeOffset.MinValue));
    }

    [Fact]
    public void AppendFilter_Null_IsNoOp()
    {
        StringBuilder sb = new StringBuilder("X");
        List<SqliteParameter> parameters = [];
        int idx = 0;

        ProcessedLocallyMapper.AppendFilter(sb, parameters, null, ref idx);

        Assert.Equal("X", sb.ToString());
        Assert.Empty(parameters);
    }

    [Fact]
    public void AppendFilter_True_AppendsIsNotNullPredicate()
    {
        StringBuilder sb = new StringBuilder();
        List<SqliteParameter> parameters = [];
        int idx = 0;

        ProcessedLocallyMapper.AppendFilter(sb, parameters, true, ref idx);

        Assert.Equal(" AND ProcessedLocallyAt IS NOT NULL", sb.ToString());
        Assert.Empty(parameters);
    }

    [Fact]
    public void AppendFilter_False_AppendsIsNullPredicate()
    {
        StringBuilder sb = new StringBuilder();
        List<SqliteParameter> parameters = [];
        int idx = 0;

        ProcessedLocallyMapper.AppendFilter(sb, parameters, false, ref idx);

        Assert.Equal(" AND ProcessedLocallyAt IS NULL", sb.ToString());
        Assert.Empty(parameters);
    }
}
