using FhirAugury.Source.Zulip.Indexing;

namespace FhirAugury.Source.Zulip.Tests;

public class ZulipQueryBuilderTests
{
    [Fact]
    public void Build_EmptyRequest_ReturnsDefaultQuery()
    {
        var request = new Fhiraugury.ZulipQueryRequest();

        var (sql, parameters) = ZulipQueryBuilder.Build(request);

        Assert.Contains("SELECT * FROM zulip_messages WHERE 1=1", sql);
        Assert.Contains("ORDER BY Timestamp DESC", sql);
        Assert.Contains("LIMIT @limit OFFSET @offset", sql);
        Assert.Equal(50, parameters.Single(p => p.ParameterName == "@limit").Value);
        Assert.Equal(0, parameters.Single(p => p.ParameterName == "@offset").Value);
    }

    [Fact]
    public void Build_StreamNameFilter_AddsInClause()
    {
        var request = new Fhiraugury.ZulipQueryRequest();
        request.StreamNames.Add("implementers");
        request.StreamNames.Add("committers");

        var (sql, _) = ZulipQueryBuilder.Build(request);

        Assert.Contains("AND StreamName IN (", sql);
    }

    [Fact]
    public void Build_StreamIdFilter_UsesSubquery()
    {
        var request = new Fhiraugury.ZulipQueryRequest();
        request.StreamIds.Add(123);

        var (sql, _) = ZulipQueryBuilder.Build(request);

        Assert.Contains("AND StreamId IN (SELECT Id FROM zulip_streams WHERE ZulipStreamId IN (", sql);
    }

    [Fact]
    public void Build_TopicExactMatch_AddsEqualsClause()
    {
        var request = new Fhiraugury.ZulipQueryRequest { Topic = "R5 ballot" };

        var (sql, parameters) = ZulipQueryBuilder.Build(request);

        Assert.Contains("AND Topic =", sql);
        Assert.Contains(parameters, p => p.Value!.ToString() == "R5 ballot");
    }

    [Fact]
    public void Build_TopicKeyword_UsesLike()
    {
        var request = new Fhiraugury.ZulipQueryRequest { TopicKeyword = "ballot" };

        var (sql, parameters) = ZulipQueryBuilder.Build(request);

        Assert.Contains("AND Topic LIKE", sql);
        Assert.Contains(parameters, p => p.Value!.ToString() == "%ballot%");
    }

    [Fact]
    public void Build_SenderNameFilter_AddsInClause()
    {
        var request = new Fhiraugury.ZulipQueryRequest();
        request.SenderNames.Add("Grahame Grieve");

        var (sql, _) = ZulipQueryBuilder.Build(request);

        Assert.Contains("AND SenderName IN (", sql);
    }

    [Fact]
    public void Build_FtsQuery_AddsFtsSubquery()
    {
        var request = new Fhiraugury.ZulipQueryRequest { Query = "patient resource" };

        var (sql, _) = ZulipQueryBuilder.Build(request);

        Assert.Contains("zulip_messages_fts MATCH", sql);
    }

    [Fact]
    public void Build_AllowedSortColumn_UsesThatColumn()
    {
        var request = new Fhiraugury.ZulipQueryRequest { SortBy = "stream_name" };

        var (sql, _) = ZulipQueryBuilder.Build(request);

        Assert.Contains("ORDER BY StreamName", sql);
    }

    [Fact]
    public void Build_DisallowedSortColumn_DefaultsToTimestamp()
    {
        var request = new Fhiraugury.ZulipQueryRequest { SortBy = "malicious_column" };

        var (sql, _) = ZulipQueryBuilder.Build(request);

        Assert.Contains("ORDER BY Timestamp", sql);
    }

    [Fact]
    public void Build_CombinedFilters_AllPresent()
    {
        var request = new Fhiraugury.ZulipQueryRequest
        {
            Topic = "test-topic",
            TopicKeyword = "key",
            Query = "search term",
        };
        request.StreamNames.Add("general");
        request.SenderNames.Add("Alice");

        var (sql, _) = ZulipQueryBuilder.Build(request);

        Assert.Contains("AND StreamName IN (", sql);
        Assert.Contains("AND Topic =", sql);
        Assert.Contains("AND Topic LIKE", sql);
        Assert.Contains("AND SenderName IN (", sql);
        Assert.Contains("zulip_messages_fts MATCH", sql);
    }

    [Fact]
    public void Build_LimitOverMax_CappedAt1000()
    {
        var request = new Fhiraugury.ZulipQueryRequest { Limit = 9999 };

        var (_, parameters) = ZulipQueryBuilder.Build(request);

        Assert.Equal(1000, parameters.Single(p => p.ParameterName == "@limit").Value);
    }
}
