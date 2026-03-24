using FhirAugury.Orchestrator.Search;

namespace FhirAugury.Orchestrator.Tests;

public class ScoreNormalizerTests
{
    [Fact]
    public void Normalize_SingleSource_MinMaxWithinGroup()
    {
        List<ScoredItem> items = new List<ScoredItem>
        {
            MakeItem("jira", "A", 10.0),
            MakeItem("jira", "B", 20.0),
            MakeItem("jira", "C", 30.0),
        };

        List<ScoredItem> result = ScoreNormalizer.Normalize(items);

        Assert.Equal(3, result.Count);
        ScoredItem a = result.Single(r => r.Id == "A");
        ScoredItem b = result.Single(r => r.Id == "B");
        ScoredItem c = result.Single(r => r.Id == "C");

        Assert.Equal(0.0, a.Score, precision: 5);
        Assert.Equal(0.5, b.Score, precision: 5);
        Assert.Equal(1.0, c.Score, precision: 5);
    }

    [Fact]
    public void Normalize_MultipleSources_NormalizedIndependently()
    {
        List<ScoredItem> items = new List<ScoredItem>
        {
            MakeItem("jira", "J1", 100.0),
            MakeItem("jira", "J2", 200.0),
            MakeItem("zulip", "Z1", 5.0),
            MakeItem("zulip", "Z2", 15.0),
        };

        List<ScoredItem> result = ScoreNormalizer.Normalize(items);

        // Both J2 and Z2 should be 1.0 (max within their group)
        ScoredItem j2 = result.Single(r => r.Id == "J2");
        ScoredItem z2 = result.Single(r => r.Id == "Z2");
        Assert.Equal(1.0, j2.Score, precision: 5);
        Assert.Equal(1.0, z2.Score, precision: 5);

        // Both J1 and Z1 should be 0.0 (min within their group)
        ScoredItem j1 = result.Single(r => r.Id == "J1");
        ScoredItem z1 = result.Single(r => r.Id == "Z1");
        Assert.Equal(0.0, j1.Score, precision: 5);
        Assert.Equal(0.0, z1.Score, precision: 5);
    }

    [Fact]
    public void Normalize_SingleItemPerSource_ScoreIsOne()
    {
        List<ScoredItem> items = new List<ScoredItem>
        {
            MakeItem("jira", "J1", 42.0),
            MakeItem("zulip", "Z1", 7.0),
        };

        List<ScoredItem> result = ScoreNormalizer.Normalize(items);

        Assert.All(result, r => Assert.Equal(1.0, r.Score, precision: 5));
    }

    [Fact]
    public void Normalize_AllSameScore_AllNormalizedToOne()
    {
        List<ScoredItem> items = new List<ScoredItem>
        {
            MakeItem("jira", "A", 5.0),
            MakeItem("jira", "B", 5.0),
            MakeItem("jira", "C", 5.0),
        };

        List<ScoredItem> result = ScoreNormalizer.Normalize(items);

        Assert.All(result, r => Assert.Equal(1.0, r.Score, precision: 5));
    }

    [Fact]
    public void Normalize_EmptyInput_ReturnsEmpty()
    {
        List<ScoredItem> result = ScoreNormalizer.Normalize([]);
        Assert.Empty(result);
    }

    [Fact]
    public void Normalize_PreservesItemMetadata()
    {
        List<ScoredItem> items = new List<ScoredItem>
        {
            MakeItem("jira", "J1", 10.0, title: "Test Issue", url: "https://jira.hl7.org/browse/FHIR-1"),
        };

        List<ScoredItem> result = ScoreNormalizer.Normalize(items);

        ScoredItem item = Assert.Single(result);
        Assert.Equal("jira", item.Source);
        Assert.Equal("J1", item.Id);
        Assert.Equal("Test Issue", item.Title);
        Assert.Equal("https://jira.hl7.org/browse/FHIR-1", item.Url);
    }

    private static ScoredItem MakeItem(string source, string id, double score,
        string title = "Title", string url = "https://example.com") => new()
    {
        Source = source,
        Id = id,
        Title = title,
        Snippet = "snippet",
        Score = score,
        Url = url,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
