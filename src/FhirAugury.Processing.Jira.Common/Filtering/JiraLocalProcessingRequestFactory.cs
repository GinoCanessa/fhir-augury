using FhirAugury.Common.Api;

namespace FhirAugury.Processing.Jira.Common.Filtering;

public sealed class JiraLocalProcessingRequestFactory
{
    public JiraLocalProcessingListRequest CreateListRequest(
        ResolvedJiraProcessingFilters filters,
        int? limit = null,
        int? offset = null,
        bool runBacked = false)
    {
        ArgumentNullException.ThrowIfNull(filters);
        return new JiraLocalProcessingListRequest
        {
            Statuses = ToList(filters.TicketStatuses),
            Projects = ToList(filters.Projects),
            Specifications = ToList(filters.Specifications),
            WorkGroups = ToList(filters.WorkGroups),
            Types = ToList(filters.TicketTypes),
            ProcessedLocally = runBacked ? null : false,
            Limit = limit,
            Offset = offset,
        };
    }

    private static List<string>? ToList(IReadOnlyList<string>? values) => values is null ? null : [.. values];
}
