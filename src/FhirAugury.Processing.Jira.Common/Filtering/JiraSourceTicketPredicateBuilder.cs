namespace FhirAugury.Processing.Jira.Common.Filtering;

public static class JiraSourceTicketPredicateBuilder
{
    public static Func<IJiraProcessingTicketFilterCandidate, bool> Build(ResolvedJiraProcessingFilters filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        HashSet<string>? statuses = ToSet(filters.TicketStatuses);
        HashSet<string>? projects = ToSet(filters.Projects);
        HashSet<string>? workGroups = ToSet(filters.WorkGroups);
        HashSet<string>? types = ToSet(filters.TicketTypes);
        string shape = filters.SourceTicketShape;

        return candidate =>
            string.Equals(candidate.SourceTicketShape, shape, StringComparison.OrdinalIgnoreCase) &&
            Matches(projects, candidate.Project) &&
            Matches(statuses, candidate.Status) &&
            Matches(workGroups, candidate.WorkGroup) &&
            Matches(types, candidate.Type);
    }

    private static HashSet<string>? ToSet(IReadOnlyList<string>? values) =>
        values is null ? null : new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);

    private static bool Matches(HashSet<string>? restrictions, string value) =>
        restrictions is null || restrictions.Contains(value);
}
