using FhirAugury.Processing.Jira.Common.Filtering;

namespace FhirAugury.Processing.Jira.Common.Discovery;

public interface IJiraTicketLabelMatcher
{
    /// <summary>
    /// Checks one batch of at most 500 distinct keys against source-owned label
    /// text. Empty batches return no matches without contacting the source.
    /// </summary>
    Task<IReadOnlyList<string>> MatchKeysAsync(
        IReadOnlyList<string> keys,
        ResolvedJiraProcessingFilters filters,
        CancellationToken ct);
}
