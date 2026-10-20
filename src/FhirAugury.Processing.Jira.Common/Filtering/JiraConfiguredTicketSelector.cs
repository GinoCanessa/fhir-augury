using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;

namespace FhirAugury.Processing.Jira.Common.Filtering;

public sealed class JiraConfiguredTicketSelector(
    JiraProcessingSourceTicketStore sourceStore,
    IJiraTicketLabelMatcher labelMatcher)
{
    public async Task<IReadOnlyList<JiraProcessingSourceTicketRecord>> SelectAsync(
        ResolvedJiraProcessingFilters filters,
        int maxItems,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ct.ThrowIfCancellationRequested();
        if (!filters.HasLabelTextFilters)
        {
            return await sourceStore.GetLocalAuthoringCandidatesAsync(filters, maxItems, ct);
        }

        IReadOnlyList<JiraProcessingSourceTicketRecord> candidates =
            await sourceStore.GetLocalAuthoringCandidatesAsync(filters, maxItems: null, ct);
        List<JiraProcessingSourceTicketRecord> selected = [];
        foreach (JiraProcessingSourceTicketRecord[] batch in candidates.Chunk(500))
        {
            ct.ThrowIfCancellationRequested();
            string[] keys = batch.Select(candidate => candidate.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            IReadOnlyList<string> matches = await labelMatcher.MatchKeysAsync(keys, filters, ct);
            ct.ThrowIfCancellationRequested();
            HashSet<string> matchingKeys = matches.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (JiraProcessingSourceTicketRecord candidate in batch)
            {
                if (matchingKeys.Contains(candidate.Key))
                {
                    selected.Add(candidate);
                    if (selected.Count >= maxItems)
                    {
                        return selected;
                    }
                }
            }
        }
        return selected;
    }
}
