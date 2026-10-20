using FhirAugury.Common.Api;

namespace FhirAugury.Processing.Jira.Common.Filtering;

public sealed class JiraLocalProcessingRequestFactory
{
    internal const int MaximumLabelMatchKeys = 500;

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

    public JiraLocalProcessingSelectionRequest CreateSelectionRequest(
        ResolvedJiraProcessingFilters filters,
        int? limit = null,
        int? offset = null,
        bool runBacked = false)
    {
        ArgumentNullException.ThrowIfNull(filters);
        return new JiraLocalProcessingSelectionRequest
        {
            Statuses = ToList(filters.TicketStatuses),
            Projects = ToList(filters.Projects),
            Specifications = ToList(filters.Specifications),
            WorkGroups = ToList(filters.WorkGroups),
            Types = ToList(filters.TicketTypes),
            ProcessedLocally = runBacked ? null : false,
            LabelText = CreateLabelTextFilter(filters),
            Limit = limit,
            Offset = offset,
        };
    }

    public JiraLocalProcessingSelectionRequest CreateLabelMatchRequest(
        IReadOnlyList<string> keys,
        ResolvedJiraProcessingFilters filters)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(filters);
        if (keys.Count == 0 || keys.Count > MaximumLabelMatchKeys)
        {
            throw new ArgumentOutOfRangeException(
                nameof(keys),
                $"A label-match batch must contain between 1 and {MaximumLabelMatchKeys} keys.");
        }
        if (keys.Any(static key => string.IsNullOrWhiteSpace(key)) ||
            keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keys.Count)
        {
            throw new ArgumentException(
                "A label-match batch must contain distinct, nonblank keys.",
                nameof(keys));
        }

        return new JiraLocalProcessingSelectionRequest
        {
            Keys = [.. keys],
            LabelText = CreateLabelTextFilter(filters),
            Limit = MaximumLabelMatchKeys,
            Offset = 0,
        };
    }

    private static JiraLabelTextFilter CreateLabelTextFilter(ResolvedJiraProcessingFilters filters)
        => new()
        {
            Includes = ToList(filters.LabelsToInclude),
            Excludes = ToList(filters.LabelsToExclude),
        };

    private static List<string>? ToList(IReadOnlyList<string>? values) => values is null ? null : [.. values];
}
