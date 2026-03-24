using FhirAugury.Orchestrator.Database;
using FhirAugury.Orchestrator.Database.Records;
using Microsoft.Data.Sqlite;

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
    /// Uses a single connection for all lookups to avoid per-item connection overhead.
    /// </summary>
    public List<ScoredItem> Boost(IEnumerable<ScoredItem> items, double boostFactor)
    {
        List<ScoredItem> itemList = items.ToList();
        if (itemList.Count == 0) return itemList;

        using SqliteConnection connection = database.OpenConnection();
        List<ScoredItem> results = new List<ScoredItem>(itemList.Count);

        // Batch: query all xref counts for all items using the single connection
        foreach (ScoredItem? item in itemList)
        {
            List<CrossRefLinkRecord> outgoing = CrossRefLinkRecord.SelectList(connection,
                SourceType: item.Source, SourceId: item.Id);
            List<CrossRefLinkRecord> incoming = CrossRefLinkRecord.SelectList(connection,
                TargetType: item.Source, TargetId: item.Id);
            int xrefCount = outgoing.Count + incoming.Count;

            double boosted = item.Score * (1.0 + boostFactor * Math.Log(1.0 + xrefCount));
            results.Add(item with { Score = boosted });
        }

        return results;
    }
}
