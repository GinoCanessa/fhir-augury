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
        IEnumerable<IGrouping<string, ScoredItem>> groups = items.GroupBy(i => i.Source);
        List<ScoredItem> results = new List<ScoredItem>();

        foreach (IGrouping<string, ScoredItem> group in groups)
        {
            List<ScoredItem> list = group.ToList();
            if (list.Count == 0) continue;

            double min = list.Min(i => i.Score);
            double max = list.Max(i => i.Score);
            double range = max - min;

            foreach (ScoredItem? item in list)
            {
                double normalized = range > 0 ? (item.Score - min) / range : 1.0;
                results.Add(item with { Score = normalized });
            }
        }

        return results;
    }
}

public record ScoredItem
{
    public required string Source { get; init; }
    public string ContentType { get; init; } = "";
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Snippet { get; init; }
    public required double Score { get; init; }
    public required string Url { get; init; }
    public required DateTimeOffset? UpdatedAt { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}
