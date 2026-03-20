using FhirAugury.Orchestrator.Database;
using FhirAugury.Orchestrator.Database.Records;

namespace FhirAugury.Orchestrator.Search;

/// <summary>
/// Applies cross-reference score boost to search results.
/// Items with more cross-references receive higher scores.
/// Formula: boosted = normalized × (1 + boost_factor × log(1 + xref_count))
/// </summary>
public class CrossRefBooster(OrchestratorDatabase database)
{
    /// <summary>
    /// Boosts scores for items that have cross-references in the database.
    /// </summary>
    public List<ScoredItem> Boost(IEnumerable<ScoredItem> items, double boostFactor)
    {
        using var connection = database.OpenConnection();
        var results = new List<ScoredItem>();

        foreach (var item in items)
        {
            var outgoing = CrossRefLinkRecord.SelectList(connection,
                SourceType: item.Source, SourceId: item.Id);
            var incoming = CrossRefLinkRecord.SelectList(connection,
                TargetType: item.Source, TargetId: item.Id);
            var xrefCount = outgoing.Count + incoming.Count;

            var boosted = item.Score * (1.0 + boostFactor * Math.Log(1.0 + xrefCount));
            results.Add(item with { Score = boosted });
        }

        return results;
    }
}
