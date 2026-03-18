using FhirAugury.Models;
using FhirAugury.Indexing;

namespace FhirAugury.Indexing.Tests;

public class ScoreNormalizerTests
{
    [Fact]
    public void Normalize_EmptyList_DoesNothing()
    {
        var results = new List<SearchResult>();
        ScoreNormalizer.Normalize(results);
        Assert.Empty(results);
    }

    [Fact]
    public void Normalize_SingleResult_SetsScoreToOne()
    {
        var results = new List<SearchResult>
        {
            new SearchResult { Source = "jira", Id = "FHIR-1", Title = "Test", Score = 5.0 },
        };

        ScoreNormalizer.Normalize(results);
        Assert.Equal(1.0, results[0].NormalizedScore);
    }

    [Fact]
    public void Normalize_AllSameScore_SetsAllToOne()
    {
        var results = new List<SearchResult>
        {
            new SearchResult { Source = "jira", Id = "A", Title = "A", Score = 3.0 },
            new SearchResult { Source = "jira", Id = "B", Title = "B", Score = 3.0 },
            new SearchResult { Source = "jira", Id = "C", Title = "C", Score = 3.0 },
        };

        ScoreNormalizer.Normalize(results);
        Assert.All(results, r => Assert.Equal(1.0, r.NormalizedScore));
    }

    [Fact]
    public void Normalize_DifferentScores_ScalesToZeroOne()
    {
        var results = new List<SearchResult>
        {
            new SearchResult { Source = "jira", Id = "A", Title = "Low", Score = 1.0 },
            new SearchResult { Source = "jira", Id = "B", Title = "Mid", Score = 5.0 },
            new SearchResult { Source = "jira", Id = "C", Title = "High", Score = 10.0 },
        };

        ScoreNormalizer.Normalize(results);

        Assert.Equal(0.0, results[0].NormalizedScore);
        Assert.True(results[1].NormalizedScore > 0.0 && results[1].NormalizedScore < 1.0);
        Assert.Equal(1.0, results[2].NormalizedScore);
    }

    [Fact]
    public void Normalize_MultipleSourceGroups_NormalizesIndependently()
    {
        var results = new List<SearchResult>
        {
            new SearchResult { Source = "jira", Id = "J1", Title = "Jira Low", Score = 1.0 },
            new SearchResult { Source = "jira", Id = "J2", Title = "Jira High", Score = 10.0 },
            new SearchResult { Source = "zulip", Id = "Z1", Title = "Zulip Low", Score = 100.0 },
            new SearchResult { Source = "zulip", Id = "Z2", Title = "Zulip High", Score = 200.0 },
        };

        ScoreNormalizer.Normalize(results);

        // Jira: J1=0.0, J2=1.0
        Assert.Equal(0.0, results[0].NormalizedScore);
        Assert.Equal(1.0, results[1].NormalizedScore);

        // Zulip: Z1=0.0, Z2=1.0
        Assert.Equal(0.0, results[2].NormalizedScore);
        Assert.Equal(1.0, results[3].NormalizedScore);
    }

    [Fact]
    public void Normalize_WithSourceWeights_AppliesMultiplier()
    {
        var results = new List<SearchResult>
        {
            new SearchResult { Source = "jira", Id = "J1", Title = "Jira", Score = 10.0 },
            new SearchResult { Source = "zulip", Id = "Z1", Title = "Zulip", Score = 10.0 },
        };

        var weights = new Dictionary<string, double>
        {
            ["jira"] = 1.0,
            ["zulip"] = 0.8,
        };

        ScoreNormalizer.Normalize(results, weights);

        // Both are single results so base normalized = 1.0
        Assert.Equal(1.0, results[0].NormalizedScore);   // jira: 1.0 * 1.0
        Assert.Equal(0.8, results[1].NormalizedScore);   // zulip: 1.0 * 0.8
    }
}
