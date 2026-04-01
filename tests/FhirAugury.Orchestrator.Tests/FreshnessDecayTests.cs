using FhirAugury.Common;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Search;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Tests;

public class FreshnessDecayTests
{
    private static IOptions<OrchestratorOptions> CreateOptions(Dictionary<string, double>? weights = null) => Options.Create(new OrchestratorOptions
    {
        Search = new SearchOptions
        {
            FreshnessWeights = weights ?? new Dictionary<string, double>
            {
                [SourceSystems.Jira] = 0.5,
                [SourceSystems.Zulip] = 2.0,
            },
        },
    });

    [Fact]
    public void Apply_RecentItem_MinimalDecay()
    {
        FreshnessDecay decay = new FreshnessDecay(CreateOptions());
        List<ScoredItem> items = new List<ScoredItem>
        {
            MakeItem(SourceSystems.Jira, 1.0, DateTimeOffset.UtcNow.AddHours(-1)),
        };

        List<ScoredItem> result = decay.Apply(items);

        // Very recent item should have score very close to 1.0
        Assert.InRange(result[0].Score, 0.99, 1.0);
    }

    [Fact]
    public void Apply_OldItem_SignificantDecay()
    {
        FreshnessDecay decay = new FreshnessDecay(CreateOptions());
        List<ScoredItem> items = new List<ScoredItem>
        {
            MakeItem(SourceSystems.Zulip, 1.0, DateTimeOffset.UtcNow.AddYears(-2)),
        };

        List<ScoredItem> result = decay.Apply(items);

        // 2 years old with weight 2.0: decay = 1/(1 + 2.0 * 4) = 1/9 ≈ 0.11
        Assert.InRange(result[0].Score, 0.05, 0.2);
    }

    [Fact]
    public void Apply_NoUpdatedAt_DecayIsOne()
    {
        FreshnessDecay decay = new FreshnessDecay(CreateOptions());
        List<ScoredItem> items = new List<ScoredItem>
        {
            MakeItem(SourceSystems.Jira, 0.75, updatedAt: null),
        };

        List<ScoredItem> result = decay.Apply(items);

        Assert.Equal(0.75, result[0].Score, precision: 5);
    }

    [Fact]
    public void Apply_DifferentSourceWeights_ApplyCorrectly()
    {
        FreshnessDecay decay = new FreshnessDecay(CreateOptions());
        DateTimeOffset oneYearAgo = DateTimeOffset.UtcNow.AddYears(-1);

        List<ScoredItem> items = new List<ScoredItem>
        {
            MakeItem(SourceSystems.Jira, 1.0, oneYearAgo),   // weight 0.5
            MakeItem(SourceSystems.Zulip, 1.0, oneYearAgo),  // weight 2.0
        };

        List<ScoredItem> result = decay.Apply(items);

        // Jira (0.5 weight) decays less than Zulip (2.0 weight)
        double jiraScore = result.Single(r => string.Equals(r.Source, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase)).Score;
        double zulipScore = result.Single(r => string.Equals(r.Source, SourceSystems.Zulip, StringComparison.OrdinalIgnoreCase)).Score;
        Assert.True(jiraScore > zulipScore,
            $"Jira ({jiraScore:F4}) should decay less than Zulip ({zulipScore:F4})");
    }

    [Fact]
    public void Apply_UnknownSource_UsesDefaultWeight()
    {
        FreshnessDecay decay = new FreshnessDecay(CreateOptions());
        List<ScoredItem> items = new List<ScoredItem>
        {
            MakeItem("unknown_source", 1.0, DateTimeOffset.UtcNow.AddYears(-1)),
        };

        List<ScoredItem> result = decay.Apply(items);

        // Default weight is 1.0: decay = 1/(1 + 1.0 * 1) = 0.5
        Assert.InRange(result[0].Score, 0.45, 0.55);
    }

    [Fact]
    public void Apply_PreservesItemMetadata()
    {
        FreshnessDecay decay = new FreshnessDecay(CreateOptions());
        List<ScoredItem> items = new List<ScoredItem>
        {
            MakeItem(SourceSystems.Jira, 1.0, DateTimeOffset.UtcNow, id: "J1", title: "Test"),
        };

        List<ScoredItem> result = decay.Apply(items);

        Assert.Equal("J1", result[0].Id);
        Assert.Equal("Test", result[0].Title);
        Assert.Equal(SourceSystems.Jira, result[0].Source);
    }

    private static ScoredItem MakeItem(string source, double score,
        DateTimeOffset? updatedAt = null, string id = "X", string title = "Title") => new()
    {
        Source = source,
        Id = id,
        Title = title,
        Snippet = "snippet",
        Score = score,
        Url = "https://example.com",
        UpdatedAt = updatedAt,
    };
}
