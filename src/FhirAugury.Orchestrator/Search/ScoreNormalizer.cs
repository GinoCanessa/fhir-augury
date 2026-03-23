namespace FhirAugury.Orchestrator.Search;

/// <summary>
/// Min-max normalization across source groups.
/// FTS5/BM25 scores from different corpora are not directly comparable,
/// so we normalize within each source group before merging.
/// </summary>
public static class ScoreNormalizer
{
    /// <summary>
    /// Applies min-max normalization within each source group.
    /// Items are grouped by source, normalized within each group, then merged.
    /// </summary>
    public static List<ScoredItem> Normalize(IEnumerable<ScoredItem> items)
    {
        var groups = items.GroupBy(i => i.Source);
        var results = new List<ScoredItem>();

        foreach (var group in groups)
        {
            var list = group.ToList();
            if (list.Count == 0) continue;

            var min = list.Min(i => i.Score);
            var max = list.Max(i => i.Score);
            var range = max - min;

            foreach (var item in list)
            {
                var normalized = range > 0 ? (item.Score - min) / range : 1.0;
                results.Add(item with { Score = normalized });
            }
        }

        return results;
    }
}

public record ScoredItem
{
    public required string Source { get; init; }
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Snippet { get; init; }
    public required double Score { get; init; }
    public required string Url { get; init; }
    public required DateTimeOffset? UpdatedAt { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}
