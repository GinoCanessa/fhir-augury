using FhirAugury.Common.Api;

namespace FhirAugury.Processing.Jira.Common.Filtering;

public sealed class JiraLocalProcessingRequestFactory
{
    public JiraLocalProcessingListRequest CreateListRequest(ResolvedJiraProcessingFilters filters, int? limit = null, int? offset = null)
    {
        ArgumentNullException.ThrowIfNull(filters);
        return new JiraLocalProcessingListRequest
        {
            Statuses = ToList(filters.TicketStatuses),
            Projects = ToList(filters.Projects),
            WorkGroups = ToList(filters.WorkGroups),
            Types = ToList(filters.TicketTypes),
            ProcessedLocally = false,
            Limit = limit,
            Offset = offset,
        };
    }

    private static List<string>? ToList(IReadOnlyList<string>? values) => values is null ? null : [.. values];
}
